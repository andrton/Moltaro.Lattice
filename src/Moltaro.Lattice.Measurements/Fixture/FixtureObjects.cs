using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Measurements.Fixture;

// The records of the reference fixture as objects of its classes, field by field, for the scenarios that
// write through a class what the fixture generates as records. Each object holds values of its own: a List
// is copied, so that nothing an object holds is shared with its record.
internal static class FixtureObjects
{
    // A user of the scalar form, or of the full form without its Groups.
    public static FixtureUser User(LatticeRecord record) => new()
    {
        Id = (long)record["Id"]!,
        Login = (string)record["Login"]!,
        Email = (string)record["Email"]!,
        DisplayName = (string)record["DisplayName"]!,
        Region = (string)record["Region"]!,
        IsActive = (bool)record["IsActive"]!,
    };

    // A user of the full form.
    public static FixtureUserWithGroups UserWithGroups(LatticeRecord record) => new()
    {
        Id = (long)record["Id"]!,
        Login = (string)record["Login"]!,
        Email = (string)record["Email"]!,
        DisplayName = (string)record["DisplayName"]!,
        Region = (string)record["Region"]!,
        IsActive = (bool)record["IsActive"]!,
        Groups = [.. record.Get<List<long>>("Groups")!],
    };

    // A group of the scalar form.
    public static FixtureGroup Group(LatticeRecord record) => new()
    {
        Id = (long)record["Id"]!,
        Name = (string)record["Name"]!,
    };

    // A group of the full form.
    public static FixtureGroupWithRoles GroupWithRoles(LatticeRecord record) => new()
    {
        Id = (long)record["Id"]!,
        Name = (string)record["Name"]!,
        Roles = [.. record.Get<List<long>>("Roles")!],
    };
}
