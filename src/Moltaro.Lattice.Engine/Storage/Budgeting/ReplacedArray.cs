namespace Moltaro.Lattice.Storage.Budgeting;

// An array a structure replaced while a read may still hold it, kept with its charge until the reads
// that could hold it have left (MemoryBudget.ReleaseAfterReads): the array, its element type and its
// length, which the inspection of the tests reports, the bytes it is charged, and the epoch of the
// protection of reads it waits for, 0 until the retirement of the database tags it.
internal readonly record struct ReplacedArray(object Array, Type ElementType, int Length, long Bytes, long Epoch);
