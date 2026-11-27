namespace Moltaro.Lattice.Storage.Budgeting;

// One managed array that a structure which grows with the data holds, as that structure reports it
// (ReportHeld): the structure, the element type and the length. A List<T> is reported by its
// Capacity. A test sizes each one by its own rule to check the charge of the budget against what
// the engine holds.
internal readonly record struct HeldArray(string Owner, Type ElementType, int Length);
