namespace Moltaro.Lattice.Budgeting;

// An earlier owner kept until the writes that reserved a replacement settle.
internal sealed class BackingHistory<T>(T backing, BackingHistory<T>? earlier)
    where T : class
{
    public static long Bytes => MemoryBudget.ObjectBytes(2L * IntPtr.Size);
    public readonly T Backing = backing;
    public BackingHistory<T>? Earlier = earlier;

    public static BackingHistory<T> Keep(T backing, BackingHistory<T>? earlier, MemoryBudget budget, string owner)
    {
        budget.Reserve(Bytes);
        try
        {
            var history = new BackingHistory<T>(backing, earlier);
            budget.Own(history, owner);
            return history;
        }
        catch
        {
            budget.Release(Bytes);
            throw;
        }
    }

    public BackingHistory<T>? Drop(MemoryBudget budget)
    {
        var earlier = Earlier;
        Earlier = null;
        budget.Release(Bytes, this);
        return earlier;
    }
}
