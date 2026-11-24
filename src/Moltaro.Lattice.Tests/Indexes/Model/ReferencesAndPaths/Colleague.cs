namespace Moltaro.Lattice.Tests.Indexes.Model.ReferencesAndPaths;

// Bound to the entity type "Colleague" of ReferenceAndPathSchema.References: Manager, a nullable Reference to a
// Colleague, held as the key of its target, an Int64.
internal sealed class Colleague
{
    public long Id { get; set; }

    public long? Manager { get; set; }
}
