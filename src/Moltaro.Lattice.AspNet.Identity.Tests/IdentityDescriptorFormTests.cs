using System.Text.Json.Nodes;
using Moltaro.Lattice.TestSupport;

namespace Moltaro.Lattice.AspNet.Identity.Tests;

// The default declaration of the Identity data in the form of the descriptor with entity
// types and index identities, and the check of a schema compiled from it.
public sealed class IdentityDescriptorFormTests
{
    [Fact]
    public void Default_descriptor_lists_entity_types_and_gives_every_index_an_identity()
    {
        var json = LatticeIdentitySchema.CreateDescriptor<IdentityUser, IdentityRole>();
        var descriptor = JsonNode.Parse(json)!;

        Assert.Null(descriptor["Tables"]);

        // The identity of every index, as the descriptor declares it.
        var declared = new List<string>();
        foreach (var entityType in descriptor["EntityTypes"]!.AsArray())
        {
            foreach (var index in entityType!["Indexes"]!.AsArray())
            {
                declared.Add($"{entityType["Name"]!.GetValue<string>()}.{index!["Name"]!.GetValue<string>()} {index["Id"]!.GetValue<int>()}");
            }
        }

        var schema = LatticeSchema.Compile(json);

        // Every index has the identity the descriptor gives it, positive and unique in its
        // entity type.
        Assert.Equal(
            declared,
            schema.EntityTypes.SelectMany(entityType => entityType.Indexes, (entityType, index) => $"{entityType.Name}.{index.Name} {index.Id}"));
        Assert.All(schema.EntityTypes, entityType =>
        {
            Assert.All(entityType.Indexes, index => Assert.InRange(index.Id, 1, int.MaxValue));
            Assert.Distinct(entityType.Indexes.Select(index => index.Id));
        });

        // The classes are bound in this form too, and the stores accept the schema.
        Assert.True(schema.TryGetEntityType(typeof(IdentityUser), out _));
        Assert.True(schema.TryGetEntityType(typeof(IdentityRole), out _));
        LatticeIdentitySchema.Validate<IdentityUser, IdentityRole>(schema);
    }

    [Fact]
    public void Default_descriptor_has_the_same_fingerprints_whatever_classes_it_is_bound_to()
    {
        var standard = LatticeSchema.Compile(LatticeIdentitySchema.CreateDescriptor<IdentityUser, IdentityRole>());
        var application = LatticeSchema.Compile(LatticeIdentitySchema.CreateDescriptor<AppUser, AppRole>());

        // The .NET class decides nothing about how the data is stored, compared or kept unique.
        Assert.Equal(standard.Fingerprint, application.Fingerprint);
        Assert.Equal(
            standard.EntityTypes.Select(entityType => entityType.LayoutFingerprint),
            application.EntityTypes.Select(entityType => entityType.LayoutFingerprint));
    }
}
