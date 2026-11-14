# Technical references

These sources support the design discussion; they are not runtime dependencies.
Individual decision records retain their more specific source links.


- [Apache Arrow columnar format](https://arrow.apache.org/docs/format/Columnar.html):
  a reference for typed buffers and dictionary encodings, not a dependency or
  a mutation/concurrency implementation.
- [PAX: Weaving Relations for Cache Performance](https://www.vldb.org/conf/2001/P169.pdf):
  a reference for grouping column values within bounded pages.
- [Memory-mapped files in .NET](https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files):
  local file mapping, separate from network hydration.
- [Kubernetes persistent volumes](https://kubernetes.io/docs/concepts/storage/persistent-volumes/):
  volume lifecycle and access modes.
- [.NET collection and unmanaged resources](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals#unmanaged-resources):
  the boundary between CLR-managed objects and engine-owned native allocations.
- [Managed large-object compaction](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.gcsettings.largeobjectheapcompactionmode?view=net-10.0):
  compaction of the managed large-object heap, separate from record packing inside an arena.
- [GC.AddMemoryPressure](https://learn.microsoft.com/en-us/dotnet/api/system.gc.addmemorypressure?view=net-10.0):
  unmanaged-consumption reporting for managed collection scheduling.
- [G1 region evacuation](https://docs.oracle.com/en/java/javase/25/gctuning/garbage-first-g1-garbage-collector1.html):
  an example of selecting productive regions and moving live objects; algorithmic
  background, not a selected collector, implementation or pause guarantee.
