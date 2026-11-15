using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage;

// The control entry of one root lifetime: a lookup reaches a root through its entry, never
// through a native address (Q16, "Stable control entry per root"). Entries live in the managed
// pages of the root table. They are runtime metadata of one process: rebuilt on load, never
// persisted or replicated. The fields, their widths and their order are those of the milestone
// contract ("Native layout and allocation"); 48 bytes, asserted by a test.
//
// Sequential layout keeps the declared order. In it every field sits on its natural boundary, so
// natural alignment adds no padding and the entry keeps its 48 bytes.
[StructLayout(LayoutKind.Sequential)]
internal struct RootEntry
{
    // The address of the root's block. It changes when the root is replaced or moved; the entry,
    // and with it every handle of the lifetime, stays where it is.
    public ArenaAddress Root;

    // The sequence of the position at which the root last changed: the position that the revision
    // of the root is formed from (milestone contract, "Write coordinator and revision semantics").
    public ulong LastMutationSequence;

    // The address of the descriptor of the root's primary key, through which the primary index
    // reaches the key.
    public ArenaAddress KeyDescriptor;

    // The identity under which the storage provider keeps this root (milestone contract,
    // "Independent base, journal and recovery"). It is neither a public key nor a native handle,
    // and a reinserted entity gets a new one.
    public long StoredRootId;

    // The generation of the slot. A handle matches the entry only while it carries this
    // generation.
    public uint Generation;

    // The identity of the root's entity type in the schema.
    public uint TypeId;

    // Where the slot and its lifetime stand.
    public RootStateEnum State;

    // How many lookups hold a temporary claim on this lifetime. While one does, the slot is not
    // reused (arena protocol, "Lock and lifetime order").
    public int ClaimCount;
}
