namespace Moltaro.Lattice.Tests.Indexes.Model.Composites;

// Bound to the entity type "Visit" of CompositeSchema, whose index ByKey has four parts, (Confirmed,
// Room, At, Note): a nullable Boolean, a UInt32 never null, a nullable DateTimeOffset and a nullable String
// compared Ordinal.
internal sealed class Visit
{
    public long Id { get; set; }

    public bool? Confirmed { get; set; }

    public uint Room { get; set; }

    public DateTimeOffset? At { get; set; }

    public string? Note { get; set; }
}
