namespace Moltaro.Lattice.Storage.Text;

// A managed allocation referenced by a locator leaf. Identity distinguishes
// shared backing from an allocation held only by a test-local reader witness.
internal readonly record struct TextLocatorAllocation(object Identity, TextAllocationShape Shape);
