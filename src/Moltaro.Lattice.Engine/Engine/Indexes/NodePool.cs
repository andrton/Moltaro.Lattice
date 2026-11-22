using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Indexes;

// The nodes reserved for the publications of one index (OrderedTree), apart from its tree: prepared blocks
// of the arena that no reader can reach, and the places in the retired list for the nodes the publications
// retire. docs/design/ordered-index.md, "The reservation" and "The standing reserve", says why it is enough.
//
// What it holds. While changes wait — inserts and removes reserved and not published or withdrawn — it
// holds at least the lesser of two bounds of what their publications can take, each true by itself:
// - their need at the tree as it is now (Need), which a publication never leaves above what is left;
// - what the linear bound (Bound) of the changes reserved since the base leaves after the nodes taken
//   since then, kept by a ledger, because that bound is amortised over a sequence of publications and
//   cannot be computed again at the tree after each of them.
// The base is the tree as it was when nothing waited, and it moves to the tree as it is whenever nothing
// waits or the linear bound of the changes waiting, from the tree as it is, is below what the ledger leaves.
// The same holds for the places, with PlacesBound in the place of Bound. Once a preparation has touched the
// index it keeps the standing reserve as well, the need of one change of key, which a top-up refills and a
// withdrawal or a refusal gives back. A Debug build asserts after every change that it holds the lesser
// bound of the changes waiting, and no more than the larger of that and the reserve (AssertReserved).
//
// The reserved nodes are linked through their own payload, the address of the next one in its first bytes,
// so that the pool takes no managed room for them: what a reservation takes is the arena's alone, and a
// reservation the arena refuses leaves nothing behind (entry F3 of docs/work/refactoring.md).
//
// The tree gives its height and the room of its root to every member that needs them; only the publishing
// thread calls it, under the lock of the writes.
internal sealed class NodePool
{
    private readonly IndexLayout _layout;

    private readonly Arena _arena;

    private readonly RetiredMemory _retired;

    // The first reserved node, null when none is, and how many there are.
    private ArenaAddress _head;

    private int _count;

    // The inserts and the removes reserved and not published or withdrawn, and the places in the retired
    // list held for them.
    private int _inserts;

    private int _removes;

    private int _places;

    // The ledger (see the class); a value, so that a reservation that is not made puts it back as it was.
    private Ledger _ledger;

    public NodePool(IndexLayout layout, Arena arena, RetiredMemory retired)
    {
        _layout = layout;
        _arena = arena;
        _retired = retired;
    }

    // The inserts reserved and not published or withdrawn, from which the tree sizes the path of its writer.
    public int Inserts => _inserts;

    // In the hold that prepares changes: reserves the nodes and the places that the inserts and the removes
    // given can take beside those waiting, the lesser bound of them all (see the class). True when it is made;
    // false, with nothing taken and the pool as it was, when the arena cannot hold the nodes, or a bound of
    // them cannot be counted as an Int32. An OutOfMemoryException of the native allocator or of the managed
    // heap leaves nothing taken either.
    public bool TryReserve(int inserts, int removes, int height, int room)
    {
        var ledger = _ledger;

        if (_inserts == 0 && _removes == 0)
        {
            Rebase(height);
        }

        _inserts += inserts;
        _removes += removes;
        _ledger.ChargedInserts += inserts;
        _ledger.ChargedRemoves += removes;

        var (nodes, places) = Targets(height, room);
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
                    if (places > _places)
                    {
                        _retired.Reserve((int)places - _places);
                        _places = (int)places;
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
    // above the larger of the bound of the changes still waiting and the standing reserve goes back.
    public void AfterPublication(int retired, int height, int room)
    {
        _places -= retired;
        _ledger.Used += retired;
        Trim(height, room, keepsReserve: true);
    }

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
    public static int Growth(int inserts, int room, int leastSeparators) =>
        inserts <= room ? 0 : 1 + ((inserts - 1 - room) / leastSeparators);

    // The most nodes the inserts and the removes given can take from a tree whose height was the one given
    // when nothing waited on it, over every order of their publications (docs/design/ordered-index.md, "The
    // reservation"): an insert that splits s levels takes 2 s + 1 nodes, at most h splits at the levels the
    // tree had, and the splits at all the levels above them together fewer than the inserts; a remove takes
    // at most two, a merged leaf and the copy of its parent. Linear in the changes, so that a ledger of it can
    // be charged change by change.
    public static long Bound(int height, long inserts, long removes) =>
        (inserts * ((2L * height) + 3)) + (2 * removes);

    // The most places in the retired list the same publications can use: an insert retires no more nodes than
    // it takes, each a node it copied; a remove at most the nodes on its path and the neighbour of a merge, at
    // a height the inserts raise by at most 1 + log2 p levels. Not linear in the inserts, so the ledger charges
    // it for the changes reserved since the base together, never change by change.
    public static long PlacesBound(int height, long inserts, long removes) =>
        (inserts * ((2L * height) + 3)) + (removes * (height + 2L + BitOperations.Log2((ulong)Math.Max(inserts, 1))));

    // The standing reserve at the tree as it is now: the need of one change of key, an insert and a remove.
    private long StandingReserve(int height, int room) => Need(height, room, _layout.LeastSeparators, inserts: 1, removes: 1);

    // What the pool holds at least for the changes waiting, nodes and places: the lesser of their need at the
    // tree and what the ledger leaves (see the class). The base moves to the tree as it is first when that
    // leaves less.
    private (long Nodes, long Places) Targets(int height, int room)
    {
        if (Bound(height, _inserts, _removes) <= _ledger.NodesLeft && PlacesBound(height, _inserts, _removes) <= _ledger.PlacesLeft)
        {
            _ledger = new Ledger { BaseHeight = height, ChargedInserts = _inserts, ChargedRemoves = _removes };
        }

        var need = Need(height, room, _layout.LeastSeparators, _inserts, _removes);

        return (Math.Min(need, _ledger.NodesLeft), Math.Min(need, _ledger.PlacesLeft));
    }

    // The ledger begins at the tree as it is, with nothing charged, taken or used.
    private void Rebase(int height) => _ledger = new Ledger { BaseHeight = height };

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
        if (_inserts == 0 && _removes == 0)
        {
            Rebase(height);
        }

        var (nodes, places) = Targets(height, room);
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
    // nothing either (TryAllocate, Undo).
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

    // Takes a prepared node from the arena into the pool; false when the arena refuses it.
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
        var need = Need(height, room, _layout.LeastSeparators, _inserts, _removes);
        var nodes = Math.Min(need, _ledger.NodesLeft);
        var places = Math.Min(need, _ledger.PlacesLeft);
        var most = Math.Max(nodes, StandingReserve(height, room));

        Debug.Assert(_ledger.NodesLeft >= 0 && _ledger.PlacesLeft >= 0, $"The publications of the index {_layout.Index.Name} took {_ledger.Taken} nodes and used {_ledger.Used} places since the base, beyond the linear bounds of what was charged.");
        Debug.Assert(_count >= nodes, $"The index {_layout.Index.Name} holds {_count} nodes for changes that can take {nodes}.");
        Debug.Assert(_count <= most, $"The index {_layout.Index.Name} holds {_count} nodes, above the larger of the bound of the changes waiting, {nodes}, and the standing reserve.");
        Debug.Assert(_places >= places, $"The index {_layout.Index.Name} holds {_places} places in the retired list for changes that can use {places}.");
    }

    // The ledger: the height of the tree at the base, the inserts and the removes reserved since then and not
    // withdrawn, and the nodes taken and the places used since then.
    private struct Ledger
    {
        public int BaseHeight;

        public long ChargedInserts;

        public long ChargedRemoves;

        public long Taken;

        public long Used;

        // What the linear bounds of the changes charged leave after what was taken and used.
        public readonly long NodesLeft => Bound(BaseHeight, ChargedInserts, ChargedRemoves) - Taken;

        public readonly long PlacesLeft => PlacesBound(BaseHeight, ChargedInserts, ChargedRemoves) - Used;
    }
}
