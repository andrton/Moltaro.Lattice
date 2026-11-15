using Moltaro.Lattice.Access;
using Moltaro.Lattice.Measurements;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Fixture;

// The records of each form of the reference fixture, generated once for the whole test run and
// shared by the tests that read them. Generating 121,000 records takes long enough that every test
// doing it again would make the default lane slow; the tests that state what a second run gives
// generate again themselves. This is static state on purpose: it is filled once, under the lock of
// Lazy, and no test changes what it holds; a test that needs another value changes a copy.
internal static class GeneratedRecords
{
    private static readonly Dictionary<string, Lazy<(LatticeSchema Schema, IReadOnlyList<LatticeRecord> Records)>> ByForm = new()
    {
        [FixtureForms.Full] = new(() => Generate(FixtureForms.Full)),
        [FixtureForms.Scalar] = new(() => Generate(FixtureForms.Scalar)),
        [FixtureForms.ScalarUnique] = new(() => Generate(FixtureForms.ScalarUnique)),
    };

    // The schema the records were generated for, and the records in the order Generate yielded them.
    public static (LatticeSchema Schema, IReadOnlyList<LatticeRecord> Records) Of(string form) => ByForm[form].Value;

    // The records of one entity type, in the order Generate yielded them.
    public static List<LatticeRecord> Of(string form, string entityTypeName) =>
        [.. Of(form).Records.Where(record => record.EntityType.Name == entityTypeName)];

    private static (LatticeSchema, IReadOnlyList<LatticeRecord>) Generate(string form)
    {
        var schema = FixtureForms.Compile(form);

        return (schema, ReferenceFixture.Generate(schema).ToList());
    }
}
