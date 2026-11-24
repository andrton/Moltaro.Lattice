namespace Moltaro.Lattice.Measurements.Fixture;

// A user of the full form of the reference fixture as an object of a class, for the scenarios that write
// and read the users of that form through a class: the fields of FixtureUser and Groups, the keys of the
// three groups of the user, as a List of references by an Int64 key is held. Named as the ClrType of the
// entity type User of the full form, it changes neither fingerprint of the schema.
internal sealed class FixtureUserWithGroups
{
    public long Id { get; set; }

    public string Login { get; set; } = "";

    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Region { get; set; } = "";

    public bool IsActive { get; set; }

    public List<long> Groups { get; set; } = [];
}
