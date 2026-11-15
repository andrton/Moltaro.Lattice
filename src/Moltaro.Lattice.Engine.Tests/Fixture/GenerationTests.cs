using System.Text.Json.Nodes;
using Moltaro.Lattice.Measurements;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Fixture;

// What ReferenceFixture.Generate yields (brief of S05, Acceptance and "The fixture"; milestone
// contract, "Reproducible first fixture"): for a schema of either form, 121,000 records, with the
// counts of the table for each entity type, every field set because none is nullable; the same
// records in the same order on every run, which Digest confirms. A schema that is neither form is
// ArgumentException, whether Generate refuses it at the call or when its records are first asked
// for. The recorded digest of seed 42 is in DigestTests.
public sealed class GenerationTests
{
    public static TheoryData<string> Forms => FixtureForms.All;

    // The full form and the scalar form. A second run of the scalar form with its Unique marks is
    // left out to keep the default lane fast; its records are counted and checked like the others.
    public static TheoryData<string> TwoForms => new(FixtureForms.Full, FixtureForms.Scalar);

    [Theory]
    [MemberData(nameof(Forms))]
    public void Generate_yields_121000_records_with_the_counts_of_the_table(string form)
    {
        var records = GeneratedRecords.Of(form).Records;

        Assert.Equal(121_000, records.Count);
        Assert.Equal(
            FixtureForms.Counts.OrderBy(count => count.Key, StringComparer.Ordinal),
            records.CountBy(record => record.EntityType.Name).OrderBy(count => count.Key, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void Every_record_is_of_an_entity_type_of_the_schema_and_has_every_field_set(string form)
    {
        var (schema, records) = GeneratedRecords.Of(form);

        foreach (var record in records)
        {
            Assert.Same(FixtureForms.EntityType(schema, record.EntityType.Name), record.EntityType);

            foreach (var field in record.EntityType.Fields)
            {
                Assert.NotNull(record[field.Name]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(TwoForms))]
    public void A_second_run_yields_the_same_records_in_the_same_order_and_the_same_digest(string form)
    {
        var first = GeneratedRecords.Of(form).Records;

        // Another run: the schema compiled again from the same descriptor, the records generated again.
        var second = ReferenceFixture.Generate(FixtureForms.Compile(form)).ToList();

        Assert.Equal(first.Count, second.Count);

        for (var index = 0; index < first.Count; index++)
        {
            Assert.True(RecordValues.Equal(first[index], second[index]), $"The record at {index} differs between the two runs.");
        }

        Assert.Equal(ReferenceFixture.Digest(first), ReferenceFixture.Digest(second));
    }

    [Fact]
    public void The_seed_is_42()
    {
        Assert.Equal(42, ReferenceFixture.Seed);
    }

    [Fact]
    public void A_schema_that_is_neither_form_is_refused()
    {
        var unrelated = LatticeSchema.Compile("""
            { "EntityTypes": [ { "Id": 1, "Name": "User", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }
            """);

        // The scalar form in a UTF-16 database: the same entity types, but not a schema compiled
        // from either descriptor of the fixture.
        var descriptor = JsonNode.Parse(ReferenceFixture.ScalarSchemaJson(unique: false))!.AsObject();
        descriptor["StringEncoding"] = "Utf16";
        var utf16 = LatticeSchema.Compile(descriptor.ToJsonString());

        foreach (var schema in new[] { unrelated, utf16 })
        {
            Assert.Throws<ArgumentException>(() =>
            {
                foreach (var _ in ReferenceFixture.Generate(schema))
                {
                }
            });
        }
    }
}
