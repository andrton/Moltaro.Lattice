namespace Moltaro.Lattice.Measurements.Fixture;

// A group of the scalar form of the reference fixture as an object of a class, for the scenarios that
// write groups through a class: the entity type Group with this class named as its ClrType, which changes
// neither fingerprint of the schema.
internal sealed class FixtureGroup
{
    public long Id { get; set; }

    public string Name { get; set; } = "";
}
