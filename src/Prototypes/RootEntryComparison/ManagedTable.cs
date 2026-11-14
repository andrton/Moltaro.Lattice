namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal sealed class ManagedTable : IRootTable<ManagedAccess>
{
    private RootEntry[][] _pages = [];
    private int _pageCount;
    private long _managedDirectoryBytes;
    private long _managedPageBytes;

    public int Capacity => _pageCount * TableGeometry.EntriesPerPage;
    public int DirectoryCapacity => _pages.Length;
    public long NativeBytes => 0;
    public long ManagedStorageBytes => _managedDirectoryBytes + _managedPageBytes;

    public void AddPage()
    {
        if (_pageCount >= TableGeometry.MaximumPages)
        {
            throw new InvalidOperationException("Prototype page budget exhausted.");
        }

        if (_pageCount == _pages.Length)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            Array.Resize(ref _pages, Math.Max(4, _pages.Length * 2));
            _managedDirectoryBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }

        var pageAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        _pages[_pageCount] = new RootEntry[TableGeometry.EntriesPerPage];
        _managedPageBytes += GC.GetAllocatedBytesForCurrentThread() - pageAllocationBefore;
        _pageCount++;
    }

    public ManagedAccess GetAccess(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Capacity);
        return new ManagedAccess(_pages, count);
    }

    public void Dispose()
    {
        _pages = [];
        _pageCount = 0;
        _managedDirectoryBytes = 0;
        _managedPageBytes = 0;
    }
}
