using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Entities;

// What a search through an index remembers between two steps once it returns more than the entities of
// one step: the primary keys of the entities it has returned or passed over by Offset (ReturnedKeys, the
// set an enumeration has), so that it returns none twice, also one deleted and created again while it
// runs or one whose key moves ahead of it in the index (docs/decisions/queries.md, Q07.3.5); the entry it
// stands after, in detached form, which owns its text (Storage.Ordered.IndexLayout.Detach), with the room
// where a step detaches the entry it is about to return; and, for FindAsync, the bytes its answer holds
// until it returns. A search that finds one entity or none never makes one: it is made at the first step
// that returns an entity without seeing the end of the search, or that has to remember a key or stand at
// an entry (IndexSearch), and from then on everything the answer holds counts, the first entity included.
//
// Managed memory of the search alone: the engine holds no reference to it, and it holds nothing of the
// engine, so a search that is abandoned leaves nothing behind. What it holds counts against the memory
// limit of a read (ReadLimits): the bytes of its returned keys by their rule, the bytes of the arrays of its
// two entries, and the bytes of the answer. Room is made outside the protection of reads, as for the
// returned keys: a step asks whether it has room, and when it has none it leaves the protection, the room
// is counted and made, and the step reads its entry anew.
//
// Staged, then committed: a step detaches the entry of the entity it returns into the room for it, stages
// its key and, for FindAsync under a memory limit, everything stored for it (RootReader.EntityBytes), and
// the search commits them once the entity is built (Commit), so that an entity whose build fails is read
// again by the next step. The two entries are two arrays of one
// capacity, which a commit swaps.
internal sealed class WalkProgress
{
    // The capacity of the first arrays of the entries: enough for an entry of fixed parts of most indexes.
    private const int FirstEntryBytes = 64;

    private byte[] _last = [];

    private int _lastLength;

    private byte[] _staged = [];

    private int _stagedLength = -1;

    private bool _keyStaged;

    private long _stagedHeld;

    // The progress of a walk of the entity type whose primary keys are given, that has returned nothing.
    public WalkProgress(in PrimaryKeys keys) => Returned = new ReturnedKeys(keys);

    // The primary keys the walk has returned or passed over.
    public ReturnedKeys Returned { get; }

    // The entry the walk stands after, in detached form with the identity the step gave it; empty before it
    // has committed one.
    public ReadOnlySpan<byte> Last => _last.AsSpan(0, _lastLength);

    // The bytes it holds for the walk, by the rule of the class: the returned keys and its two entries.
    public long Bytes => Returned.Bytes + _last.Length + _staged.Length;

    // The bytes the answer of FindAsync holds, which count against the memory limit beside Bytes.
    public long Held { get; private set; }

    // Whether an entry of the length given can be detached without allocating. Inside the protection of
    // reads.
    public bool HasRoomFor(int length) => length <= _last.Length;

    // The most bytes it holds at once, the answer included, while it makes room for one more key whose text
    // takes at most keyBound code units, -1 for no key, and for an entry of the length given: the new
    // arrays of the entries beside the old ones.
    public long BytesWithRoomFor(int keyBound, int length)
    {
        var keys = keyBound < 0 ? Returned.Bytes : Returned.BytesWithRoomFor(keyBound);
        var entries = (long)_last.Length + _staged.Length;

        if (!HasRoomFor(length))
        {
            entries += 2L * CapacityFor(length);
        }

        return keys + entries + Held;
    }

    // Makes room for one more key whose text takes at most keyBound code units, -1 for none, and for an
    // entry of the length given, keeping the entry the walk stands after. Outside the protection of reads.
    public void MakeRoom(int keyBound, int length)
    {
        if (keyBound >= 0)
        {
            Returned.MakeRoom(keyBound);
        }

        if (HasRoomFor(length))
        {
            return;
        }

        var capacity = CapacityFor(length);
        var last = new byte[capacity];

        Last.CopyTo(last);
        _last = last;
        _staged = new byte[capacity];
    }

    // The room where a step writes the entry it stands at, of the length given, which it has room for: when
    // a round of a step ends before an entry, so that the next round goes on there.
    public Span<byte> StandAt(int length)
    {
        _lastLength = length;

        return _last.AsSpan(0, length);
    }

    // The room where a step detaches the entry it is about to return, of the length given, which it has
    // room for.
    public Span<byte> StageEntry(int length)
    {
        _stagedLength = length;

        return _staged.AsSpan(0, length);
    }

    // Stages the key of the entity the step is about to return, which the returned keys have room for, with
    // the bound HasRoomFor of the returned keys gave.
    public void StageKey(uint hash, in RootReader stored, int bound)
    {
        Returned.Stage(hash, stored, bound);
        _keyStaged = true;
    }

    // Stages everything stored for the entity the step is about to return, for the answer of FindAsync.
    public void StageHeld(long bytes) => _stagedHeld = bytes;

    // Remembers at once the key of an entity the walk passes over by Offset, which the returned keys have
    // room for.
    public void Remember(uint hash, in RootReader stored, int bound)
    {
        Returned.Stage(hash, stored, bound);
        Returned.Commit();
    }

    // Adds bytes the answer of FindAsync holds: the capacity its list grew by.
    public void Hold(long bytes) => Held += bytes;

    // The entity the last step staged was built and is returned: the walk stands after its entry, and
    // remembers its key and what is stored for it.
    public void Commit()
    {
        if (_stagedLength >= 0)
        {
            (_last, _staged) = (_staged, _last);
            _lastLength = _stagedLength;
            _stagedLength = -1;
        }

        if (_keyStaged)
        {
            Returned.Commit();
            _keyStaged = false;
        }

        Held += _stagedHeld;
        _stagedHeld = 0;
    }

    // Forgets what the last step staged: for a search whose owner could not build the entity the step
    // returned, so that the next step stages it again.
    public void Unstage()
    {
        _stagedLength = -1;
        _keyStaged = false;
        _stagedHeld = 0;
    }

    // The capacity of the arrays of the entries that holds an entry of the length given: twice the present
    // one, at least FirstEntryBytes and the length.
    private int CapacityFor(int length) => Math.Max(Math.Max(FirstEntryBytes, 2 * _last.Length), length);
}
