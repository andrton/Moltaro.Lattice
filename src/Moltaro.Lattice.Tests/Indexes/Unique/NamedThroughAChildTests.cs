namespace Moltaro.Lattice.Tests.Indexes.Unique;

// A write through a child that the master refuses for a unique index names the index the master named
// (decision on unique indexes, "A conflict is UniqueConstraintViolation, identifying the declared index"):
// of two violated indexes the first in the order of their declaration, also where that order is not the
// order of their identities, and the one violated when only one is. Profiles of UniqueSchemas, whose
// ByNick, Id 2, is declared before ByLogin, Id 1.
public sealed class NamedThroughAChildTests
{
    [Fact]
    public async Task Refusal_through_a_child_names_the_first_declared_violated_index_whatever_its_identity()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, UniqueSchemas.Profiles);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var profiles = master.Set("Profile");
        var copies = child.Set("Profile");

        await profiles.InsertAsync(UniqueSchemas.RecordOf(profiles, 1, ("Login", "ada"), ("Nick", "Ada")));

        await UniqueSchemas.ExpectTakenAsync("ByNick", () => copies.InsertAsync(UniqueSchemas.RecordOf(copies, 2, ("Login", "ada"), ("Nick", "ADA"))));
        await UniqueSchemas.ExpectTakenAsync("ByLogin", () => copies.InsertAsync(UniqueSchemas.RecordOf(copies, 3, ("Login", "ada"), ("Nick", "Grace"))));
        await UniqueSchemas.ExpectTakenAsync("ByNick", () => copies.InsertAsync(UniqueSchemas.RecordOf(copies, 4, ("Login", "grace"), ("Nick", "aDA"))));

        Assert.Single(profiles.Enumerate());
    }
}
