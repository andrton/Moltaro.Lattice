using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

// Synthetic identical payload for both tables, not an accepted engine layout.
[StructLayout(LayoutKind.Sequential)]
internal struct RootEntry
{
    public ulong Location { get; set; }
    public long Revision { get; set; }
    public uint Generation { get; set; }
    public int AccessState { get; set; }
    public int Retainers { get; set; }
    public int NextFree { get; set; }
}
