namespace Moltaro.Lattice.Storage;

// One value per schema field, in field order, plus the revision of that state. The array
// is never modified after creation, so a master and its children share it.
internal readonly record struct StoredEntity(object?[] Values, LatticeRevision Revision);
