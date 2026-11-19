using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Text;

// One slot of the text table (TextTable): what the table keeps of one text entry, in managed memory
// that only the writes use. The entry itself is immutable memory of the arena that holds the stored
// bytes and nothing else; what changes with the writes — the count — lies here, so that no read ever
// reads a line a write writes for a text it shares.
//
// Sequential layout: the two 8-byte fields first, then the three of 4 bytes; 32 bytes with the padding
// that keeps the next slot aligned.
[StructLayout(LayoutKind.Sequential)]
internal struct TextSlot
{
    // The state of a slot whose entry is published, in State.
    public const uint Published = 0;

    // The state of a slot whose entry is still prepared: it was allocated for a write that has not been
    // published, and no published block refers to it yet.
    public const uint Prepared = 1;

    // The relative address of the entry: its one block, or the directory of its chunks. The null
    // address for a free slot.
    public ArenaAddress Address;

    // The references to the entry: one for each Text descriptor naming it in any block of the arena that
    // is not freed, prepared, live or retired (TextTable, "One count for each entry"). At least 1 while
    // the slot holds an entry; an entry whose count reaches 0 leaves the table and is freed at once.
    public long References;

    // The seeded hash of the stored bytes, under which the bucket table holds the slot.
    public uint Hash;

    // The stored bytes of the text, in the encoding of the database; at least 1. By this length the
    // entry is placed, one block or chunks (Values.ValueBlocks), and a lookup passes over an entry of
    // another length without reading it.
    public uint Length;

    // For an entry, Prepared or Published. For a free slot, the next slot of the list of free slots
    // plus one, 0 at its end (TextTable), so that the list takes no memory beyond the slots, as the root
    // table keeps its own (RootTable).
    public uint State;
}
