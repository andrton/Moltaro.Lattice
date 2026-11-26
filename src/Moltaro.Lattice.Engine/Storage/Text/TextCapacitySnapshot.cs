namespace Moltaro.Lattice.Storage.Text;

// Groups distinguish current capacity, migration overlap and retained versions.
internal readonly record struct TextCapacitySnapshot(
    IReadOnlyList<TextAllocationShape> LocatorDirectory,
    IReadOnlyList<TextAllocationShape> LocatorLeaves,
    IReadOnlyList<TextAllocationShape> WriterMetadata,
    IReadOnlyList<TextAllocationShape> ActiveBuckets,
    IReadOnlyList<TextAllocationShape> OldBuckets,
    IReadOnlyList<TextAllocationShape> RetiredLocator);
