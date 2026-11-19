using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage;

// The control entry of one root lifetime: a lookup reaches a root through its entry, never
// through a native address (Q16, "Stable control entry per root"). Entries live in the managed
// pages of the root table. They are runtime metadata of one process, reconstructed locally and
// never persisted. The fields, their widths and their order are those of the milestone contract
// ("Native layout and allocation"); 48 bytes, asserted by a test.
//
// Sequential layout keeps the declared order. In it every field sits on its natural boundary, so
// natural alignment adds no padding and the entry keeps its 48 bytes.
//
// Reads inside the protection of reads read Root, LastMutationSequence and State while the writer
// changes them, through the members Acquire… and Release…, which read with acquire and write with
// release semantics, each field as one aligned word; Engine.Entities.ChangePublisher says in which order
// the writer writes them and Engine.Entities.EntityTable how a read takes them as one state. The other
// fields do not change while a read can reach the lifetime of the entry; the capture of a load, which
// reaches every slot by its number, reads them only once it has taken the state Live of a lifetime
// (EntityTable.TryTakeLive).
[StructLayout(LayoutKind.Sequential)]
internal struct RootEntry
{
    // The address of the root's block. It changes when the root is replaced or moved; the entry,
    // and with it every handle of the lifetime, stays where it is. The entry of a deleted root keeps
    // the address of the retired root until the entry is released, so that a read that reached the
    // entry before still compares and copies a whole root.
    public ArenaAddress Root;

    // The sequence of the position of the last published mutation of the root. It is the
    // specification's "last mutation position": the history of a position is the database's and
    // is not repeated per root.
    public ulong LastMutationSequence;

    // The address of the descriptor of the root's primary key: the entry identifies the key of its
    // root through it (arena protocol, "Ownership and addresses").
    public ArenaAddress KeyDescriptor;

    // The identity under which the storage provider keeps this root (milestone contract,
    // "Independent base, journal and recovery"). It is neither a public key nor a native handle,
    // and a reinserted entity gets a new one.
    public long StoredRootId;

    // The generation of the slot, 1 for its first lifetime. A handle matches the entry only while
    // it carries this generation.
    public uint Generation;

    // The identity of the root's entity type in the schema. In a free entry, which holds no root, it
    // is the link of the root table's list of free slots instead (RootTable): the next free slot
    // plus one, 0 at the end. RootTable.Add writes the type of the new lifetime over it.
    public uint TypeId;

    // Where the slot and its lifetime stand.
    public RootStateEnum State;

    // The four bytes the milestone contract gave to the claims of lookups. Reads protected by epochs
    // take no claim (brief of S08), so nothing uses them; they keep the entry at its 48 bytes and its
    // fields where the contract places them, until a later sprint gives them a use.
    public int Reserved;

    // The root, read with acquire semantics: a read that sees a root sees what was written before
    // the store that published it.
    public ArenaAddress AcquireRoot()
    {
        var value = Volatile.Read(ref Unsafe.As<ArenaAddress, ulong>(ref Root));

        return Unsafe.As<ulong, ArenaAddress>(ref value);
    }

    // Publishes a root, by a store with release semantics.
    public void ReleaseRoot(ArenaAddress root) => Volatile.Write(ref Unsafe.As<ArenaAddress, ulong>(ref Root), root.Value);

    // The sequence of the last mutation, read with acquire semantics.
    public ulong AcquireLastMutation() => Volatile.Read(ref LastMutationSequence);

    // Writes the sequence of the last mutation, by a store with release semantics.
    public void ReleaseLastMutation(ulong sequence) => Volatile.Write(ref LastMutationSequence, sequence);

    // The state, read with acquire semantics.
    public RootStateEnum AcquireState() => (RootStateEnum)Volatile.Read(ref Unsafe.As<RootStateEnum, uint>(ref State));

    // Writes the state, by a store with release semantics.
    public void ReleaseState(RootStateEnum state) => Volatile.Write(ref Unsafe.As<RootStateEnum, uint>(ref State), (uint)state);
}
