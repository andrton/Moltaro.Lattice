using System.Collections;
using System.Text.RegularExpressions;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Measurements;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Fixture;

// ReferenceFixture.Digest (brief of S05, "The fixture"): the SHA-256 of the values of the records,
// as lowercase hexadecimal, in an encoding that does not depend on the order in which a Set or a
// Map enumerates. By it a run shows that it generated the same fixture as every other, so it
// changes when a value changes; the order of a List is part of its value. That a second run gives
// the same digest is in GenerationTests. Digest takes any records; to keep the default lane fast,
// these tests digest a part of the fixture, which is records like any other, except the test of the
// recorded digests, which digests the whole fixture that GeneratedRecords holds already.
public sealed class DigestTests
{
    // The digests of the fixture of seed 42, recorded from the implementation of S05. A recorded
    // value that changes means the fixture changed: every figure measured on it before is no longer
    // comparable with one measured after. A value is therefore changed only on purpose, together
    // with the coordinator of the epic, and never to make this test pass.
    private const string FullFormDigest = "f8794f7d00404b4984e84770d564d525364a5b180ae99f9e0e552a1313d96e6e";

    // One digest for the scalar form with and without its Unique marks: the marks belong to the
    // indexes of the schema, not to the records, and the digest is taken over the values of the
    // records only.
    private const string ScalarFormDigest = "4dc1a0d6c53c1111ad75afd84f475ccce39bc367a7d09bd85a94ffecdfbbbf06";

    // The Acceptance of S05: the generator produces the same 121,000 roots from seed 42 on every
    // run, checked by a digest; on every run means in every process and on every machine.
    [Theory]
    [InlineData(FixtureForms.Full, FullFormDigest)]
    [InlineData(FixtureForms.Scalar, ScalarFormDigest)]
    [InlineData(FixtureForms.ScalarUnique, ScalarFormDigest)]
    public void The_fixture_of_seed_42_has_its_recorded_digest(string form, string digest)
    {
        Assert.Equal(digest, ReferenceFixture.Digest(GeneratedRecords.Of(form).Records));
    }

    [Fact]
    public void The_digest_is_the_lowercase_hexadecimal_of_a_SHA256()
    {
        foreach (var form in new[] { FixtureForms.Full, FixtureForms.Scalar })
        {
            var records = GeneratedRecords.Of(form).Records;

            Assert.Matches(new Regex("^[0-9a-f]{64}$"), ReferenceFixture.Digest(records.Take(1_000)));
            Assert.Matches(new Regex("^[0-9a-f]{64}$"), ReferenceFixture.Digest([records[^1]]));
        }
    }

    [Fact]
    public void The_digest_does_not_depend_on_the_order_in_which_a_Set_or_a_Map_enumerates()
    {
        var role = GeneratedRecords.Of(FixtureForms.Full, "Role")[0];
        var setting = GeneratedRecords.Of(FixtureForms.Full, "Setting")[0];

        foreach (var record in new[] { role, setting })
        {
            // The same members given in the reverse order: the record builds its Set or Map anew, and
            // it enumerates them in another order.
            var field = CollectionField(record, LatticeFieldTypeEnum.Set, LatticeFieldTypeEnum.Map);
            var reordered = RecordValues.Copy(record);
            reordered[field.Name] = field.Declaration.Type == LatticeFieldTypeEnum.Set
                ? ((IEnumerable<string>)record[field.Name]!).Reverse().ToList()
                : ((IEnumerable<KeyValuePair<string, string>>)record[field.Name]!).Reverse().ToList();

            Assert.NotEqual(Members(record[field.Name]!), Members(reordered[field.Name]!));
            Assert.True(RecordValues.Equal(record, reordered));

            Assert.Equal(ReferenceFixture.Digest([record]), ReferenceFixture.Digest([reordered]));
        }
    }

    [Fact]
    public void The_digest_changes_when_a_value_changes()
    {
        var records = GeneratedRecords.Of(FixtureForms.Full, "User").Take(1_000).ToList();
        var digest = ReferenceFixture.Digest(records);

        // A user whose List of groups reads differently backwards.
        var index = Enumerable.Range(0, records.Count).First(position => !IsPalindrome(Groups(records[position])));
        var user = records[index];

        // A scalar value of that record, in a copy that takes its place.
        var changed = RecordValues.Copy(user);
        changed["IsActive"] = !(bool)user["IsActive"]!;

        Assert.NotEqual(digest, ReferenceFixture.Digest(Replaced(records, index, changed)));

        // The order of the elements of its List.
        var reordered = RecordValues.Copy(user);
        reordered[CollectionField(user, LatticeFieldTypeEnum.List).Name] = Enumerable.Reverse(Groups(user)).ToList();

        Assert.NotEqual(digest, ReferenceFixture.Digest(Replaced(records, index, reordered)));
    }

    private static List<long> Groups(LatticeRecord user) =>
        [.. (IEnumerable<long>)user[CollectionField(user, LatticeFieldTypeEnum.List).Name]!];

    private static bool IsPalindrome(List<long> elements) => elements.SequenceEqual(Enumerable.Reverse(elements));

    // The records with one of them in place of the record at the index.
    private static IEnumerable<LatticeRecord> Replaced(IReadOnlyList<LatticeRecord> records, int index, LatticeRecord replacement) =>
        records.Select((record, position) => position == index ? replacement : record);

    // The one field of the record of one of these types, whose name the table leaves open.
    private static LatticeField CollectionField(LatticeRecord record, params LatticeFieldTypeEnum[] types) =>
        record.EntityType.Fields.Single(field => types.Contains(field.Declaration.Type));

    // The members of a Set or the keys of a Map, in the order they enumerate.
    private static List<object?> Members(object collection) =>
        collection is IDictionary map ? [.. map.Keys.Cast<object?>()] : [.. ((IEnumerable)collection).Cast<object?>()];
}
