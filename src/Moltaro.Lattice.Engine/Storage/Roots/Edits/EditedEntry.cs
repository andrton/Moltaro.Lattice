namespace Moltaro.Lattice.Storage.Roots.Edits;

// An original entry by ordinal, or new values. A replacement of a Map entry
// keeps its original key bytes, including the spelling of a String key.
internal readonly record struct EditedEntry(int Original, object? First, object? Second, bool ReplacesSecond = false)
{
    public const int New = -1;
    public const int Removed = -2;

    public static EditedEntry Stored(int ordinal) => new(ordinal, null, null);
}
