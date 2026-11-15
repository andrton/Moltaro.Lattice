using Moltaro.Lattice.Measurements;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Fixture;

// The three schemas of the reference fixture, by a name a theory can carry: the full form, and the
// scalar form with and without its Unique marks (ReferenceFixture). Each is compiled afresh.
internal static class FixtureForms
{
    public const string Full = "Full";

    public const string Scalar = "Scalar";

    public const string ScalarUnique = "ScalarUnique";

    // The counts of the table of the milestone contract, "Reproducible first fixture".
    public static readonly IReadOnlyDictionary<string, int> Counts = new Dictionary<string, int>
    {
        ["User"] = 100_000,
        ["Group"] = 10_000,
        ["Role"] = 1_000,
        ["Setting"] = 10_000,
    };

    public static TheoryData<string> All => new(Full, Scalar, ScalarUnique);

    public static string Json(string form) => form switch
    {
        Full => ReferenceFixture.SchemaJson,
        Scalar => ReferenceFixture.ScalarSchemaJson(unique: false),
        ScalarUnique => ReferenceFixture.ScalarSchemaJson(unique: true),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, "Not a form of the fixture."),
    };

    public static LatticeSchema Compile(string form) => LatticeSchema.Compile(Json(form));

    public static LatticeEntityType EntityType(LatticeSchema schema, string name)
    {
        Assert.True(schema.TryGetEntityType(name, out var entityType), $"The fixture has no entity type {name}.");

        return entityType;
    }

    public static LatticeField Field(LatticeEntityType entityType, string name)
    {
        Assert.True(entityType.TryGetField(name, out var field), $"The entity type {entityType.Name} has no field {name}.");

        return field;
    }
}
