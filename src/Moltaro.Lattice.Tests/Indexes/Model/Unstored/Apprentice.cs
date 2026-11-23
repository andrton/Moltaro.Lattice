namespace Moltaro.Lattice.Tests.Indexes.Model.Unstored;

// Bound to the entity type "Apprentice" of UnstoredSchema.References: Mentor, a Reference to a Colleague that
// is never null, held as the key of its target, an Int64.
internal sealed class Apprentice
{
    public long Id { get; set; }

    public long Mentor { get; set; }
}
