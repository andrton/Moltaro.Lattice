using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Fixture;

// The records of the reference fixture (milestone contract, "Reproducible first fixture"), for a
// schema of either form: the users first, then the groups, the roles and the settings, each type by
// its identity from 1, all drawn in that order from one FixtureRandom of the seed, which every
// enumeration starts afresh.
//
// The text formats are those of the table, the digits being the identity of the record: Login
// user-00000001, Email u00000001@example.test, DisplayName User 00000001, group-00001, role-0001,
// the key of a setting setting-00001 and its Name key-00001. What the table leaves open is chosen
// here:
// - Region of a user is one of region-01 to region-16, Section of a setting one of section-01 to
//   section-16, and IsActive of a user is true or false, each drawn;
// - the three groups of a user and the two roles of a group are consecutive identities from a drawn
//   start, wrapping from the last identity of the target type to its first;
// - the eight permissions of a role are distinct, drawn from permission-01 to permission-64, in the
//   order drawn; the three options of a setting are distinct pairs of option-NN and value-NN, NN from
//   01 to 16, in the order drawn;
// - the Value of a setting is 128 bytes of UTF-8: letters and digits, and in every tenth setting,
//   those whose identity is a multiple of 10, a mix that starts with a character of two, three or
//   four bytes and is filled with letters to its length.
// The scalar form draws the values of the collections as well and leaves them out, so that its
// records are the records of the full form without their collection fields.
internal static class FixtureRecords
{
    public const int Users = 100_000;

    public const int Groups = 10_000;

    public const int Roles = 1_000;

    public const int Settings = 10_000;

    // The bytes of the Value of a setting.
    public const int ValueBytes = 128;

    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    // How many characters WideCharacter has.
    private const int WideCharacters = 5;

    // The records for a schema of the full form when collections is true, of the scalar form
    // otherwise; ReferenceFixture has checked which form it is. The entity types are found at the call,
    // the records made while they are enumerated. Every enumeration starts a generator of its own from
    // the seed, so every enumeration of the returned sequence yields the same records.
    public static IEnumerable<LatticeRecord> Generate(LatticeSchema schema, bool collections, ulong seed)
    {
        var user = EntityType(schema, "User");
        var group = EntityType(schema, "Group");
        var role = EntityType(schema, "Role");
        var setting = EntityType(schema, "Setting");

        return Records(user, group, role, setting, collections, seed);
    }

    private static IEnumerable<LatticeRecord> Records(
        LatticeEntityType user,
        LatticeEntityType group,
        LatticeEntityType role,
        LatticeEntityType setting,
        bool collections,
        ulong seed)
    {
        // Made when the enumeration starts, not when Generate is called: a generator shared by two
        // enumerations would give the second the values after the first.
        var random = new FixtureRandom(seed);

        for (var id = 1; id <= Users; id++)
        {
            yield return User(user, id, collections, random);
        }

        for (var id = 1; id <= Groups; id++)
        {
            yield return Group(group, id, collections, random);
        }

        for (var id = 1; id <= Roles; id++)
        {
            yield return Role(role, id, collections, random);
        }

        for (var id = 1; id <= Settings; id++)
        {
            yield return Setting(setting, id, collections, random);
        }
    }

    private static LatticeRecord User(LatticeEntityType entityType, int id, bool collections, FixtureRandom random)
    {
        var digits = id.ToString("D8", CultureInfo.InvariantCulture);
        var record = new LatticeRecord(entityType)
        {
            ["Id"] = (long)id,
            ["Login"] = "user-" + digits,
            ["Email"] = "u" + digits + "@example.test",
            ["DisplayName"] = "User " + digits,
            ["Region"] = Numbered("region-", random.Below(16)),
            ["IsActive"] = random.Below(2) == 1,
        };
        var groups = Consecutive(random.Below(Groups), 3, Groups);

        if (collections)
        {
            record["Groups"] = groups;
        }

        return record;
    }

    private static LatticeRecord Group(LatticeEntityType entityType, int id, bool collections, FixtureRandom random)
    {
        var record = new LatticeRecord(entityType)
        {
            ["Id"] = (long)id,
            ["Name"] = "group-" + id.ToString("D5", CultureInfo.InvariantCulture),
        };
        var roles = Consecutive(random.Below(Roles), 2, Roles);

        if (collections)
        {
            record["Roles"] = roles;
        }

        return record;
    }

    private static LatticeRecord Role(LatticeEntityType entityType, int id, bool collections, FixtureRandom random)
    {
        var record = new LatticeRecord(entityType)
        {
            ["Id"] = (long)id,
            ["Name"] = "role-" + id.ToString("D4", CultureInfo.InvariantCulture),
        };
        var permissions = Distinct(random, 8, 64).Select(index => Numbered("permission-", index)).ToList();

        if (collections)
        {
            record["Permissions"] = permissions;
        }

        return record;
    }

    private static LatticeRecord Setting(LatticeEntityType entityType, int id, bool collections, FixtureRandom random)
    {
        var digits = id.ToString("D5", CultureInfo.InvariantCulture);
        var record = new LatticeRecord(entityType)
        {
            ["Key"] = "setting-" + digits,
            ["Section"] = Numbered("section-", random.Below(16)),
            ["Name"] = "key-" + digits,
            ["Value"] = Value(random, wide: id % 10 == 0),
        };
        var options = Distinct(random, 3, 16)
            .Select(index => KeyValuePair.Create(Numbered("option-", index), Numbered("value-", index)))
            .ToList();

        if (collections)
        {
            record["Options"] = options;
        }

        return record;
    }

    // A text with a number from 01, for the index of a value in its value set.
    private static string Numbered(string prefix, int index) =>
        prefix + (index + 1).ToString("D2", CultureInfo.InvariantCulture);

    // count consecutive identities from start + 1, wrapping from the last of the target type, targets,
    // to its first, 1.
    private static List<long> Consecutive(int start, int count, int targets) =>
        [.. Enumerable.Range(0, count).Select(step => (long)((start + step) % targets) + 1)];

    // count distinct indexes below bound, in the order drawn.
    private static List<int> Distinct(FixtureRandom random, int count, int bound)
    {
        var drawn = new List<int>(count);

        while (drawn.Count < count)
        {
            var index = random.Below(bound);

            if (!drawn.Contains(index))
            {
                drawn.Add(index);
            }
        }

        return drawn;
    }

    // ValueBytes bytes of UTF-8 text: letters and digits, or, when wide, a character of more than one
    // byte first and then a mix of both, in which a character too long for the bytes left gives way
    // to a letter.
    private static string Value(FixtureRandom random, bool wide)
    {
        // A character of UTF-16 never takes fewer bytes in UTF-8 than it has code units, so the text
        // has at most ValueBytes of them.
        Span<char> text = stackalloc char[ValueBytes];
        var length = 0;
        var bytes = 0;

        while (bytes < ValueBytes)
        {
            if (wide && (length == 0 || random.Below(2) == 0))
            {
                var (character, width) = WideCharacter(random.Below(WideCharacters));

                if (bytes + width <= ValueBytes)
                {
                    character.CopyTo(text[length..]);
                    length += character.Length;
                    bytes += width;

                    continue;
                }
            }

            text[length++] = Letters[random.Below(Letters.Length)];
            bytes++;
        }

        return new string(text[..length]);
    }

    // A character of more than one byte in UTF-8, with its bytes: é, Ж, €, 漢 and 😀, the last a
    // surrogate pair.
    private static (string Text, int Bytes) WideCharacter(int index) => index switch
    {
        0 => ("é", 2),
        1 => ("Ж", 2),
        2 => ("€", 3),
        3 => ("漢", 3),
        _ => ("😀", 4),
    };

    private static LatticeEntityType EntityType(LatticeSchema schema, string name) =>
        schema.TryGetEntityType(name, out var entityType)
            ? entityType
            : throw new ArgumentException($"The schema has no entity type {name}; it is not a form of the reference fixture.", nameof(schema));
}
