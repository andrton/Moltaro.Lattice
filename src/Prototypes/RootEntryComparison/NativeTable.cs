using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal sealed unsafe class NativeTable : IRootTable<NativeAccess>
{
    private nint[] _pages = [];
    private int _pageCount;
    private long _managedDirectoryBytes;

    public int Capacity => _pageCount * TableGeometry.EntriesPerPage;
    public int DirectoryCapacity => _pages.Length;
    public long NativeBytes => (long)_pageCount * TableGeometry.PageBytes;
    public long ManagedStorageBytes => _managedDirectoryBytes;

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

        var page = NativeMemory.AllocZeroed(TableGeometry.PageBytes);
        if (page is null)
        {
            throw new OutOfMemoryException();
        }

        _pages[_pageCount] = (nint)page;
        _pageCount++;
    }

    public NativeAccess GetAccess(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Capacity);
        return new NativeAccess(_pages, count);
    }

    public void Dispose()
    {
        for (var index = 0; index < _pageCount; index++)
        {
            NativeMemory.Free((void*)_pages[index]);
        }

        _pages = [];
        _pageCount = 0;
        _managedDirectoryBytes = 0;
    }
}
