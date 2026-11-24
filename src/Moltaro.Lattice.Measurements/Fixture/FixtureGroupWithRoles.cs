namespace Moltaro.Lattice.Measurements.Fixture;

// A group of the full form of the reference fixture as an object of a class: the fields of FixtureGroup and
// Roles, the keys of the two roles of the group, as a List of references by an Int64 key is held. Named as
// the ClrType of the entity type Group of the full form, it changes neither fingerprint of the schema.
internal sealed class FixtureGroupWithRoles
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public List<long> Roles { get; set; } = [];
}
