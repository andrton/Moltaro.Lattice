namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal static class TableGeometry
{
    public const int PageShift = 12;
    public const int EntriesPerPage = 1 << PageShift;
    public const int PageMask = EntriesPerPage - 1;
    public const int EntryBytes = 32;
    public const int PageBytes = EntriesPerPage * EntryBytes;
    public const int MaximumEntries = 262144;
    public const int MaximumPages = MaximumEntries / EntriesPerPage + 1;
}
