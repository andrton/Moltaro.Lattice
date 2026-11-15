using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Fixture;

// What the generated records hold, as far as the table of the milestone contract gives it
// ("Reproducible first fixture"): the text formats of Login, Email, DisplayName and the names; the
// value sets Region and Section are drawn from, 16 values each; a Value of 128 bytes of valid
// UTF-8, non-ASCII in a deterministic 10% of the settings; keys that are keys, and the values of a
// unique index distinct. In the full form: three Group references for a user and two Role
// references for a group, which wrap into the generated range of their target; eight permissions
// for a role from 64 values; three entries for a setting from 16 pairs. The identities and the
// synthetic value sets themselves are the implementer's and are not stated.
public sealed class RecordContentTests
{
    public static TheoryData<string> Forms => FixtureForms.All;

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void The_text_of_every_record_has_the_format_of_the_table(string form)
    {
        AssertAll(form, "User", "Login", "^user-[0-9]{8}$");
        AssertAll(form, "User", "Email", "^u[0-9]{8}@example\\.test$");
        AssertAll(form, "User", "DisplayName", "^User [0-9]{8}$");
        AssertAll(form, "Group", "Name", "^group-[0-9]{5}$");
        AssertAll(form, "Role", "Name", "^role-[0-9]{4}$");
        AssertAll(form, "Setting", "Name", "^key-[0-9]{5}$");

        foreach (var setting in GeneratedRecords.Of(form, "Setting"))
        {
            Assert.Matches("^setting-[0-9]{5}$", (string)setting[setting.EntityType.Key.Name]!);
        }
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void Region_and_Section_are_drawn_from_16_values(string form)
    {
        Assert.InRange(GeneratedRecords.Of(form, "User").Select(user => (string)user["Region"]!).Distinct().Count(), 1, 16);
        Assert.InRange(GeneratedRecords.Of(form, "Setting").Select(setting => (string)setting["Section"]!).Distinct().Count(), 1, 16);
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void Every_Value_is_128_bytes_of_valid_UTF8_and_a_tenth_of_them_is_not_ASCII(string form)
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var settings = GeneratedRecords.Of(form, "Setting");

        foreach (var setting in settings)
        {
            Assert.Equal(128, strict.GetByteCount((string)setting["Value"]!));
        }

        Assert.Equal(settings.Count / 10, settings.Count(setting => !Ascii.IsValid((string)setting["Value"]!)));
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void Keys_are_distinct_and_so_are_the_values_of_the_unique_indexes(string form)
    {
        foreach (var entityTypeName in FixtureForms.Counts.Keys)
        {
            var records = GeneratedRecords.Of(form, entityTypeName);
            var key = records[0].EntityType.Key.Name;

            Assert.Equal(records.Count, records.Select(record => record[key]).Distinct().Count());
        }

        // Unique in the full form, and in the scalar form when it is asked for its Unique marks.
        var users = GeneratedRecords.Of(form, "User");
        var roles = GeneratedRecords.Of(form, "Role");

        Assert.Equal(users.Count, users.Select(user => user["Login"]).Distinct().Count());
        Assert.Equal(roles.Count, roles.Select(role => role["Name"]).Distinct().Count());
    }

    [Fact(Skip = NotImplemented.ReferenceFixture)]
    public void References_of_the_full_form_wrap_into_the_generated_range_of_their_target()
    {
        var groupKeys = GeneratedRecords.Of(FixtureForms.Full, "Group").Select(group => group["Id"]).ToHashSet();
        var roleKeys = GeneratedRecords.Of(FixtureForms.Full, "Role").Select(role => role["Id"]).ToHashSet();

        foreach (var user in GeneratedRecords.Of(FixtureForms.Full, "User"))
        {
            var groups = CollectionOf(user).Cast<object>().ToList();

            Assert.Equal(3, groups.Count);
            Assert.All(groups, group => Assert.Contains(group, groupKeys));
        }

        foreach (var group in GeneratedRecords.Of(FixtureForms.Full, "Group"))
        {
            var roles = CollectionOf(group).Cast<object>().ToList();

            Assert.Equal(2, roles.Count);
            Assert.All(roles, role => Assert.Contains(role, roleKeys));
        }
    }

    [Fact(Skip = NotImplemented.ReferenceFixture)]
    public void Permissions_and_options_of_the_full_form_are_drawn_from_their_value_sets()
    {
        var permissions = new HashSet<object>();

        foreach (var role in GeneratedRecords.Of(FixtureForms.Full, "Role"))
        {
            var members = CollectionOf(role).Cast<object>().ToList();

            Assert.Equal(8, members.Count);
            permissions.UnionWith(members);
        }

        Assert.InRange(permissions.Count, 8, 64);

        var pairs = new HashSet<(object?, object?)>();

        foreach (var setting in GeneratedRecords.Of(FixtureForms.Full, "Setting"))
        {
            var options = (IDictionary)CollectionOf(setting);

            Assert.Equal(3, options.Count);

            foreach (DictionaryEntry entry in options)
            {
                Assert.IsType<string>(entry.Key);
                Assert.IsType<string>(entry.Value);
                pairs.Add((entry.Key, entry.Value));
            }
        }

        Assert.InRange(pairs.Count, 3, 16);
    }

    private static void AssertAll(string form, string entityTypeName, string fieldName, string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);

        foreach (var record in GeneratedRecords.Of(form, entityTypeName))
        {
            var text = (string)record[fieldName]!;

            Assert.True(regex.IsMatch(text), $"{entityTypeName}.{fieldName} '{text}' does not match {pattern}.");
        }
    }

    // The value of the one collection field of a record of the full form, whose name the table
    // leaves open.
    private static IEnumerable CollectionOf(LatticeRecord record)
    {
        var field = record.EntityType.Fields.Single(field => field.Declaration.Type is LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set or LatticeFieldTypeEnum.Map);

        return (IEnumerable)record[field.Name]!;
    }
}
