using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Indexes;

// The nodes reserved for the publications of one index (OrderedTree), apart from its tree: prepared blocks
// of the arena that no reader can reach, and the places in the retired list for the nodes the publications
// retire. docs/design/ordered-index.md, "The reservation" and "The standing reserve", says why it is enough.
//
// What it guarantees. While changes wait, inserts and removes reserved and not published or withdrawn, it
// holds, nodes and places alike, at least the lesser of two bounds of what their publications can take,
// each true by itself: their need at the tree as it is (Need), and what their bound by levels (Bounds) from
// the base of a ledger leaves after what was taken and used since the base. The bound by levels counts the
// splits each level can make from the progress of its nodes, which the pool keeps from what the tree reports
// of every node it publishes and replaces (Added, Removed). Once a preparation has touched the index it also
// keeps the standing reserve. A publication takes its nodes from the pool and allocates nothing; a
// reservation that is refused, or meets an OutOfMemoryException, leaves the pool as it was. A Debug build
// asserts after every change that the pool holds the lesser bound and no more than the larger of it and the
// reserve (AssertReserved).
//
// What protects its state. Only the publishing thread calls it, under the lock of the writes, and the tree
// gives its height and the room of its root to every member that needs them. The reserved nodes are linked
// through their own payload, the address of the next one in its first bytes, so that the pool takes no
// managed room for them.
internal sealed class NodePool
{
    private readonly IndexLayout _layout;

    private readonly Arena _arena;

    private readonly RetiredMemory _retired;

    // The bytes a node holds, as IndexNode.UsedBytes counts them, the same for a leaf and an inner node: the
    // payload after the header. The widest entry of a leaf with its offset, and the widest separator of an inner
    // node with its offset and the child after it; and the credits of the two kinds (the constructor).
    private readonly int _capacity;

    private readonly int _leafWidest;

    private readonly int _innerWidest;

    private readonly int _leafCredit;

    private readonly int _innerCredit;

    // The first reserved node, null when none is, and how many there are.
    private ArenaAddress _head;

    private int _count;

    // The inserts and the removes reserved and not published or withdrawn, and the places in the retired
    // list held for them.
    private int _inserts;

    private int _removes;

    private int _places;

    // The progress of each level of the tree, the leaves at 0: the sum of what its nodes have made of their
    // credits (docs/design/ordered-index.md, "The reservation"), for as many levels as the tree can have while
    // the changes reserved wait (MostLevels).
    private long[] _progress = [];

    // The progress of each level of the tree at the base of the ledger, as many levels as _progress holds. A
    // reservation moves the base only once nothing of it can fail, so that one that is not made leaves the
    // base as it was (TryReserve).
    private long[] _baseProgress = [];

    // The ledger (see the class); a value, so that a reservation that is not made puts it back as it was.
    private Ledger _ledger;

    // For the tests of the engine (exception X3, docs/work/exceptions.md): whether the next growth of the
    // arrays of the levels fails in place of its last allocation (ArmLevelsFault).
    private bool _levelsFault;

    public NodePool(IndexLayout layout, Arena arena, RetiredMemory retired)
    {
        _layout = layout;
        _arena = arena;
        _retired = retired;

        var offset = layout.HasText ? IndexLayout.OffsetBytes : 0;

        _capacity = layout.PayloadBytes - NodeHeader.Size;
        _leafWidest = layout.WidestEntryBytes + offset;
        _innerWidest = _leafWidest + IndexLayout.ChildBytes;

        // The credits. A leaf that splits in the middle of its bytes leaves halves of at most half of them,
        // rounded up, and one widest entry less a byte, since the first half stops before the entry that would
        // pass the middle; a new leaf may hold one entry and be the last leaf, which counts twice. An inner node
        // leaves halves of at most half the bytes of its separators, beside its first child, since the separator
        // in the middle goes up; a new root holds one separator, fewer bytes than a half. The credit of an inner
        // node is 1 at least, since it holds four of its widest separators.
        var leafHalf = ((_capacity + _leafWidest + 1) / 2) + _leafWidest - 1;
        var innerHalf = ((_capacity - IndexLayout.ChildBytes + _innerWidest) / 2) + IndexLayout.ChildBytes;

        _leafCredit = Math.Min(InsertsBeforeFull(leafHalf, _leafWidest), InsertsBeforeFull(_leafWidest, _leafWidest) / 2);
        _innerCredit = InsertsBeforeFull(innerHalf, _innerWidest);
    }

    // The inserts reserved and not published or withdrawn, from which the tree sizes the path of its writer.
    public int Inserts => _inserts;

    // The credits of a leaf and of an inner node of this index (the constructor).
    public int LeafCredit => _leafCredit;

    public int InnerCredit => _innerCredit;

    // In the hold that prepares changes: reserves the nodes and the places that the inserts and the removes
    // given can take beside those waiting, the lesser bound of them all (see the class), and the room in
    // managed memory for the progress of the levels the tree can reach while they wait. True when it is made;
    // false, with nothing taken and the pool as it was, when the arena cannot hold the nodes, or a bound of
    // them cannot be counted as an Int32. An OutOfMemoryException of the native allocator or of the managed
    // heap leaves nothing taken either.
    public bool TryReserve(int inserts, int removes, int height, int room)
    {
        var ledger = _ledger;

        _inserts += inserts;
        _removes += removes;
        _ledger.ChargedInserts += inserts;
        _ledger.ChargedRemoves += removes;

        // When the base moves, what the ledger leaves from there is the bounds of the changes waiting from
        // the tree as it is; the move itself waits until nothing of the reservation can fail.
        var (nodes, places) = Targets(height, room, LeftAfterMove(height, out var moves));
        var taken = 0;

        try
        {
            if (nodes <= int.MaxValue && places <= int.MaxValue && Fits(nodes - _count))
            {
                while (_count < nodes && TryAllocate())
                {
                    taken++;
                }

                if (_count >= nodes)
                {
                    // The levels first: an OutOfMemoryException of their arrays leaves no place reserved.
                    var levels = MostLevels(height, _inserts);

                    if (_progress.Length < levels)
                    {
                        GrowLevels(levels);
                    }

                    if (places > _places)
                    {
                        _retired.Reserve((int)places - _places);
                        _places = (int)places;
                    }

                    // Nothing fails from here: the move allocates nothing.
                    if (moves)
                    {
                        MoveBase(height);
                    }

                    AssertReserved(height, room);

                    return true;
                }
            }
        }
        catch
        {
            Undo(ledger, inserts, removes, taken);
            throw;
        }

        Undo(ledger, inserts, removes, taken);

        return false;
    }

    // Once every index of the changes has what they need: tops the pool up to the standing reserve, as far
    // as the arena gives. What the arena refuses, or the native allocator cannot supply, is not taken, and
    // nothing fails.
    public void TopUp(int height, int room)
    {
        var reserve = StandingReserve(height, room);

        try
        {
            while (_count < reserve && TryAllocate())
            {
            }
        }
        catch (OutOfMemoryException)
        {
            // The native allocator could not supply a segment: the rest of the reserve is not had, which is
            // not a failure of the write.
        }

        AssertReserved(height, room);
    }

    // The changes given are withdrawn before their publication: they leave the ledger, and every node above
    // the bound of the changes still waiting goes back to the arena, the standing reserve with them, so that
    // what a change took and did not publish never stays in the pool.
    public void Release(int inserts, int removes, int height, int room)
    {
        _inserts -= inserts;
        _removes -= removes;
        _ledger.ChargedInserts -= inserts;
        _ledger.ChargedRemoves -= removes;
        Trim(height, room, keepsReserve: false);
    }

    // Before a write is refused for memory: every node above the bound of the changes waiting goes back to
    // the arena, the standing reserve with them.
    public void GiveBackReserve(int height, int room) => Trim(height, room, keepsReserve: false);

    // In a publication: a reserved node, live from now on, with its payload, which the tree writes whole; the
    // link the pool kept in it is read before. InvalidOperationException when none is left: a publication
    // that took more than its changes reserved is a defect.
    public ArenaAddress Take(out Span<byte> payload)
    {
        if (_count == 0)
        {
            throw new InvalidOperationException($"A publication of the index {_layout.Index.Name} took more nodes than were reserved for it.");
        }

        var node = _head;

        payload = _arena.Payload(node);
        _head = MemoryMarshal.Read<ArenaAddress>(payload);
        _count--;
        _ledger.Taken++;
        _arena.Publish(node);

        return node;
    }

    // In a publication: a node of the level given, whose entries or separators take the bytes given with their
    // offsets and children (IndexNode.UsedBytes), is in the tree from now on, written whole, or is replaced and
    // leaves it; the progress of its level follows (docs/design/ordered-index.md, "The reservation"). Inlined,
    // as the progress is, so that a publication makes no call for it (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Added(int level, int usedBytes) => ProgressAt(level) += ProgressOf(level, usedBytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Removed(int level, int usedBytes) => ProgressAt(level) -= ProgressOf(level, usedBytes);

    // In a publication: an insert or a rewrite of an entry, which was reserved as an insert.
    public void TakeInsert()
    {
        if (_inserts == 0)
        {
            throw new InvalidOperationException($"An insert into the index {_layout.Index.Name} is published that reserved nothing.");
        }

        _inserts--;
    }

    // In a publication: a remove of an entry.
    public void TakeRemove()
    {
        if (_removes == 0)
        {
            throw new InvalidOperationException($"A remove from the index {_layout.Index.Name} is published that reserved nothing.");
        }

        _removes--;
    }

    // The end of a publication, which retired the nodes counted: the places they took are used, and what is
    // above the larger of the bound of the changes still waiting and the standing reserve goes back. Inlined,
    // so that the end of a publication of the tree calls the trim directly: only the ARM64 listing of the
    // NativeAOT cross-compiler gains, which left it a call; x64 inlined it already.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AfterPublication(int retired, int height, int room)
    {
        _places -= retired;
        _ledger.Used += retired;
        Trim(height, room, keepsReserve: true);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next growth of the arrays of the
    // levels, in a reservation, throws OutOfMemoryException in place of its last allocation, once, after the
    // others were made, as the managed heap can. A growth when no test armed it loads one field more, and
    // nothing else reads it.
    public void ArmLevelsFault() => _levelsFault = true;

    // The bounds by levels of the inserts and the removes given from the tree as it is, of the height given:
    // the nodes they take and the places they use, in any order of their publications (see the class).
    public (long Nodes, long Places) BoundsAt(int height, long inserts, long removes) =>
        Bounds(Levels(_progress, height), _leafCredit, _innerCredit, inserts, removes);

    // The most nodes the inserts and the removes given, waiting, can take from a tree of the height given whose
    // root has the room given, and the most places in the retired list they can use: the k-th insert counts
    // 2 H + 1 at the height H = h + Growth(k - 1) it can find, and every remove H + 1 at the height
    // h + Growth(p) after all of them (docs/design/ordered-index.md, "The reservation"). A remove never makes
    // the tree higher, nor its root fuller without making it lower by a level, so the order of the changes
    // does not matter. The sum over the inserts in closed form, so that it costs the same for any number of
    // changes waiting: with r the room, L the least separators, p the inserts and q the removes, every
    // division integer and the whole in long,
    //
    //   m = max(0, p - 1 - r), a = m / L, b = m % L, S = m + L a (a - 1) / 2 + a b,
    //   Need = p (2h + 1) + 2 S + q (h + Growth(p) + 1),
    //
    // where S is the sum of Growth(k) for k = 0 to p - 1. Growth(k) is 0 up to k = r, and for the m terms
    // beyond the room it is 1, and on top of that 0 for the first L of them, 1 for the next L, and so on:
    // a whole steps of L, which add L (0 + 1 + ... + (a - 1)), and b terms more, which add a each.
    public static long Need(int height, int room, int leastSeparators, int inserts, int removes)
    {
        long least = leastSeparators;
        var beyondRoom = Math.Max(0L, inserts - 1L - room);
        var steps = beyondRoom / least;
        var rest = beyondRoom % least;
        var growths = beyondRoom + (least * steps * (steps - 1) / 2) + (steps * rest);

        return (inserts * ((2L * height) + 1)) + (2 * growths) + (removes * (height + Growth(inserts, room, leastSeparators) + 1L));
    }

    // The rule of growth: the most levels the inserts given can add to a tree whose root has the room given,
    // the first after the room of the root, and each further one after LeastSeparators more, since the root a
    // growth makes holds one separator and one insert adds at most one separator to any node.
    private static int Growth(int inserts, int room, int leastSeparators) =>
        inserts <= room ? 0 : 1 + ((inserts - 1 - room) / leastSeparators);

    // The most levels a tree of the height given can have while the inserts given wait, published in any
    // order and among any removes: one more than the levels that can split by their bound (Bounds), since only
    // a split of the root adds a level. Every level of the tree may split, and the leaves of an empty tree;
    // above them a level has no progress and makes at most half the splits of the level below, since the
    // credit of an inner node is 1 at least, so p inserts split at most floor(log2 p) levels more. The rule of
    // growth bounds the levels as well, but grows with the inserts.
    public static int MostLevels(int height, int inserts) => Math.Max(height, 1) + 1 + BitOperations.Log2((uint)inserts);

    // The bounds by levels (docs/design/ordered-index.md, "The reservation") of the inserts and the removes
    // given from a tree whose levels have the progress given, the leaves first, one for each level of its
    // height, and whose leaves and inner nodes have the credits given. A level of credit c that gets k inserts
    // splits at most min(k, (P + k) / (c + 1)) times, P its progress, at the leaves with the second credit of
    // the last leaf and 2 c for each remove. The most nodes they take are p + 2 (s0 + s1 + ...) + 2 q, and the
    // most places they use: an insert retires the nodes it splits and the one it copies, p + s0 + s1 + ...,
    // and a remove at most the nodes of its path and the neighbour of a merge, at a height of at most one more
    // than the levels that split, or the height of the tree if more. A level above the tree has no progress,
    // and the credit of an inner node is 1 at least, since it holds four of its widest separators, so the
    // splits at least halve from one such level to the next and the loop ends. Linear in the changes or
    // better, and monotone in them, so that the ledger can charge the changes reserved since its base
    // together. Inlined, so that a single write, whose reservation and publication count it, makes no call
    // for it (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (long Nodes, long Places) Bounds(ReadOnlySpan<long> progress, int leafCredit, int innerCredit, long inserts, long removes)
    {
        var height = progress.Length;
        var splits = 0L;
        var splitting = 0;
        var arriving = inserts;

        for (var level = 0; arriving > 0; level++)
        {
            // The progress of the level, none above the tree; tested unsigned, so that the read checks no bound.
            var credit = level == 0 ? leafCredit : innerCredit;
            var made = (uint)level < (uint)progress.Length ? progress[level] : 0;

            if (level == 0)
            {
                made += leafCredit + (2L * leafCredit * removes);
            }

            arriving = Math.Min(arriving, (made + arriving) / (credit + 1));
            splits += arriving;
            splitting += arriving > 0 ? 1 : 0;
        }

        var reach = inserts == 0 ? height : Math.Max(height, splitting + 1);

        return (inserts + (2 * splits) + (2 * removes), inserts + splits + (removes * (reach + 1L)));
    }

    // The progress of a node of the level and the used bytes given (docs/design/ordered-index.md, "The
    // reservation"): the part of the credit of its kind its bytes show it has made, c - min(c, the inserts of
    // the widest item it takes before it may not fit one more).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ProgressOf(int level, int usedBytes)
    {
        var credit = level == 0 ? _leafCredit : _innerCredit;

        return credit - Math.Min(credit, InsertsBeforeFull(usedBytes, level == 0 ? _leafWidest : _innerWidest));
    }

    // The inserts of the widest item given a node of the used bytes given, which it holds, takes before it may
    // not fit one more.
    private int InsertsBeforeFull(int usedBytes, int widest) => (_capacity - usedBytes) / widest;

    // The standing reserve at the tree as it is now: the need of one change of key, an insert and a remove.
    private long StandingReserve(int height, int room) => Need(height, room, _layout.LeastSeparators, inserts: 1, removes: 1);

    // What the ledger leaves once its base has moved to the tree as it is, if it moves, and whether it moves
    // (MoveBase): when nothing waits, or when the bounds of the changes waiting, counted from the tree as it
    // is, are no more than what the ledger leaves (docs/design/ordered-index.md, "The reservation"). After a
    // move the ledger leaves those bounds, which is what Left counts from the moved base; otherwise it leaves
    // what Left counts now. With nothing published since the base the tree is the base and the changes charged
    // are those waiting, so it stays: every publication takes a node or retires one. Moves nothing itself, so
    // that a reservation moves the base only after its last step that can fail.
    private (long Nodes, long Places) LeftAfterMove(int height, out bool moves)
    {
        moves = _ledger.Taken != 0 || _ledger.Used != 0;

        if (moves && _inserts == 0 && _removes == 0)
        {
            return (0, 0);
        }

        var left = Left();

        if (moves)
        {
            var bounds = BoundsAt(height, _inserts, _removes);

            moves = bounds.Nodes <= left.Nodes && bounds.Places <= left.Places;

            if (moves)
            {
                return bounds;
            }
        }

        return left;
    }

    // The base of the ledger moves to the tree as it is: the progress of its levels is copied into the base,
    // the changes waiting are charged, and nothing is taken or used since. Allocates nothing, and fails in
    // nothing. Inlined, so that a publication, after which the base moves, makes no call for it beside
    // LeftAfterMove (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveBase(int height)
    {
        var from = Levels(_progress, height);
        var to = Levels(_baseProgress, height);

        // Both hold the levels of the height; each length is tested, so that the copy checks no bound.
        for (var level = 0; (uint)level < (uint)from.Length && (uint)level < (uint)to.Length; level++)
        {
            to[level] = from[level];
        }

        _ledger = new Ledger { Height = height, ChargedInserts = _inserts, ChargedRemoves = _removes };
    }

    // What the bounds of the changes charged since the base leave, counted from the tree at the base, after
    // what was taken and used since then. Inlined, as Bounds is (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (long Nodes, long Places) Left()
    {
        var progress = Levels(_baseProgress, _ledger.Height);
        var (nodes, places) = Bounds(progress, _leafCredit, _innerCredit, _ledger.ChargedInserts, _ledger.ChargedRemoves);

        return (nodes - _ledger.Taken, places - _ledger.Used);
    }

    // The progress of the level given, in the array of the tree as it is, which TryReserve sized for every
    // level the tree can reach while the changes reserved wait, and the progress of the levels of a tree of
    // the height given, in an array that holds them. Made without a check of the bounds, so that a single
    // write makes no call for it (JIT listing at full optimization: the throw of the check is a call of its
    // own), as PreparedWrite.Changes is; a Debug build asserts the bounds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref long ProgressAt(int level)
    {
        Debug.Assert((uint)level < (uint)_progress.Length, $"The index {_layout.Index.Name} has a node at the level {level}, beyond the {_progress.Length} levels its pool counts.");

        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_progress), level);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Span<long> Levels(long[] progress, int height)
    {
        Debug.Assert((uint)height <= (uint)progress.Length, $"A tree of {height} levels beyond the {progress.Length} its pool counts.");

        return MemoryMarshal.CreateSpan(ref MemoryMarshal.GetArrayDataReference(progress), height);
    }

    // What the pool holds at least for the changes waiting, nodes and places: the lesser of their need at the
    // tree and what the ledger leaves, given (see the class). Inlined, so that a reservation and a trim make no
    // call for it beside the count of the need (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (long Nodes, long Places) Targets(int height, int room, (long Nodes, long Places) left)
    {
        var need = Need(height, room, _layout.LeastSeparators, _inserts, _removes);

        return (Math.Min(need, left.Nodes), Math.Min(need, left.Places));
    }

    // Room for the progress of the levels given, in the array of the tree as it is and in that of the base,
    // each with what it holds. Both arrays are allocated before either is assigned, so that an
    // OutOfMemoryException of the managed heap leaves them as they were, of one length: the members that read
    // them check no bound (ProgressAt, Levels).
    private void GrowLevels(int levels)
    {
        var progress = new long[levels];

        // A test of the engine may fail the last allocation (ArmLevelsFault).
        if (_levelsFault)
        {
            _levelsFault = false;

            throw new OutOfMemoryException("The test failed the growth of the levels of the pool.");
        }

        var baseProgress = new long[levels];

        _progress.CopyTo(progress, 0);
        _baseProgress.CopyTo(baseProgress, 0);
        _progress = progress;
        _baseProgress = baseProgress;
    }

    // Gives back what a reservation that is not made took: its nodes to the arena, in the reverse of their
    // order, its changes out of the counts, and the ledger as it was before.
    private void Undo(in Ledger ledger, int inserts, int removes, int taken)
    {
        for (var node = 0; node < taken; node++)
        {
            _arena.Free(Pop());
        }

        _inserts -= inserts;
        _removes -= removes;
        _ledger = ledger;
    }

    // After a publication, a withdrawal or before a refusal: the nodes above the bound of the changes still
    // waiting go back to the arena, but for the standing reserve after a publication, and the places above it
    // to the retired list. With nothing waiting the base moves to the tree as it is.
    private void Trim(int height, int room, bool keepsReserve)
    {
        var left = LeftAfterMove(height, out var moves);

        if (moves)
        {
            MoveBase(height);
        }

        var (nodes, places) = Targets(height, room, left);
        var kept = keepsReserve ? Math.Max(nodes, StandingReserve(height, room)) : nodes;

        while (_count > kept)
        {
            _arena.Free(Pop());
        }

        if (_places > places)
        {
            _retired.Release(_places - (int)places);
            _places = (int)places;
        }

        AssertReserved(height, room);
    }

    // Whether the nodes given may fit the arena: their slots no more than what the memory limit leaves beside
    // the slots in use. A reservation of more is refused before it takes anything, so that it obtains no
    // segment for nodes it would give back; one of less may still be refused by the arena, which then takes
    // nothing either (TryAllocate, Undo). Inlined, so that a reservation makes no call for it beside the read
    // of the counters of the arena (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Fits(long nodes)
    {
        if (nodes <= 0)
        {
            return true;
        }

        var arena = _arena.Counters;
        var inUse = arena.LiveBytes + arena.ClassSlackBytes + arena.PreparedBytes + arena.RetiredBytes;

        return nodes * _layout.NodeBytes <= _arena.MemoryLimit - inUse;
    }

    // Takes a prepared node from the arena into the pool; false when the arena refuses it. Inlined into the
    // loops of a reservation and of a top-up, so that a node refilled costs the calls of the arena and no call
    // of its own (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryAllocate()
    {
        if (!_arena.TryAllocatePrepared(_layout.PayloadBytes, BlockKindEnum.IndexNode, default, out var node))
        {
            return false;
        }

        MemoryMarshal.Write(_arena.Payload(node), in _head);
        _head = node;
        _count++;

        return true;
    }

    // The first reserved node out of the pool, still prepared.
    private ArenaAddress Pop()
    {
        var node = _head;

        _head = MemoryMarshal.Read<ArenaAddress>(_arena.Payload(node));
        _count--;

        return node;
    }

    // For a Debug build, after every change of the pool: what it holds against the bounds of the changes
    // waiting and the reserve (see the class).
    [Conditional("DEBUG")]
    private void AssertReserved(int height, int room)
    {
        var (nodesLeft, placesLeft) = Left();
        var (nodes, places) = Targets(height, room, (nodesLeft, placesLeft));
        var most = Math.Max(nodes, StandingReserve(height, room));

        Debug.Assert(nodesLeft >= 0 && placesLeft >= 0, $"The publications of the index {_layout.Index.Name} took {_ledger.Taken} nodes and used {_ledger.Used} places since the base, beyond the bounds of what was charged.");
        Debug.Assert(_count >= nodes, $"The index {_layout.Index.Name} holds {_count} nodes for changes that can take {nodes}.");
        Debug.Assert(_count <= most, $"The index {_layout.Index.Name} holds {_count} nodes, above the larger of the bound of the changes waiting, {nodes}, and the standing reserve.");
        Debug.Assert(_places >= places, $"The index {_layout.Index.Name} holds {_places} places in the retired list for changes that can use {places}.");
    }

    // The ledger: the height of the tree at the base, whose levels have the progress _baseProgress holds, the
    // inserts and the removes reserved since then and not withdrawn, and the nodes taken and the places used
    // since then.
    private struct Ledger
    {
        public int Height;

        public long ChargedInserts;

        public long ChargedRemoves;

        public long Taken;

        public long Used;
    }
}
