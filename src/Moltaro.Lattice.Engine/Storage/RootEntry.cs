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
[StructLayout(LayoutKind.Sequential)]
internal struct RootEntry
{
    // The address of the root's block. It changes when the root is replaced or moved; the entry,
    // and with it every handle of the lifetime, stays where it is.
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

    // The identity of the root's entity type in the schema.
    public uint TypeId;

    // Where the slot and its lifetime stand.
    public RootStateEnum State;

    // How many lookups hold a temporary claim on this lifetime. While one does, the slot must not
    // be released (arena protocol, "Lock and lifetime order"). RootTable.Release does not look at
    // it; its caller does.
    public int ClaimCount;
}
