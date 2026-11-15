using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Tests.TextPolicies;

// Text compared without case is equal exactly when the case table of Lattice's own holds it equal
// (decision on strings, Q04, the addition of 15 November 2026; the owner's answer to Q46): scalar value
// by scalar value after the simple uppercase mapping of Unicode 16.0.0, the same in every process and
// under every globalization mode of the runtime, and never by StringComparison.OrdinalIgnoreCase of the
// running process. CaseTableTests states the table; these tests state that the comparison follows it
// wherever it shows through the public API today:
// - a String key compared without case, on the engine, in a UTF-8 and in a UTF-16 database: a read
//   under the other text finds the entity exactly when the two are one key, and an insert under it is
//   DuplicateKey exactly then (the placeholder refuses such an entity type until S12);
// - the one operation per entity of a write batch, on both implementations;
// - the keys of a Map and the members of a Set compared without case, which a record holds under the
//   comparison of the schema, in the core.
// A field compared without case that is not a key shows its comparison only through an index, which
// comes with S14, and a unique index with S16.
//
// The pairs: letters in ASCII and outside it; two that the globalization modes of .NET 10 disagree on,
// Cyrillic Tje, new in Unicode 16.0, which ICU and NLS hold different, and Deseret, which NLS holds
// different; Garay, of Unicode 16.0 and of a supplementary plane; the long s and the dotless i, whose
// simple uppercase mappings are S and I, which OrdinalIgnoreCase of .NET holds different from them;
// and four that the table holds different: sharp s and capital sharp s, which a culture and a mapping
// to lowercase hold equal; Straße and STRASSE, and the ligature ff and FF, which a full case mapping
// holds equal; the Kelvin sign and k, which a mapping to lowercase holds equal.
public sealed class EqualityWithoutCaseTests
{
    private static readonly Dictionary<string, (string Left, string Right, bool Equal)> Pairs = new()
    {
        ["ASCII letters"] = ("Main", "MAIN", true),
        ["letters outside ASCII"] = ("Été", "éTÉ", true),
        ["Cyrillic Tje"] = ("\u1C89", "\u1C8A", true),
        ["Deseret Long I"] = ("\U00010400", "\U00010428", true),
        ["Garay A"] = ("\U00010D50", "\U00010D70", true),
        ["long s"] = ("\u017Fun", "SUN", true),
        ["dotless i"] = ("\u0131", "I", true),
        ["sharp s and capital sharp s"] = ("ß", "\u1E9E", false),
        ["sharp s and SS"] = ("Straße", "STRASSE", false),
        ["ligature ff and FF"] = ("\uFB00", "FF", false),
        ["Kelvin sign and k"] = ("\u212A", "k", false),
    };

    // Account has a String key compared without case; Holder a Map and a Set of String compared without
    // case. The schema of the write batch and of the records is in UTF-8.
    private static readonly LatticeSchema Schema = SchemaIn(LatticeStringEncodingEnum.Utf8);

    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum, string>> EncodingsAndPairs =>
        from encoding in new[] { LatticeStringEncodingEnum.Utf8, LatticeStringEncodingEnum.Utf16 }
        from pair in Pairs.Keys
        select new TheoryDataRow<LatticeStringEncodingEnum, string>(encoding, pair);

    [Fact]
    public void Pairs_are_equal_or_different_by_the_simple_uppercase_mapping_of_Unicode_16_0_0()
    {
        Assert.All(Pairs, pair => Assert.Equal(pair.Value.Equal, CaseRule.Equal(pair.Value.Left, pair.Value.Right)));
    }

    [Theory]
    [MemberData(nameof(EncodingsAndPairs))]
    public async Task Two_texts_are_one_key_exactly_when_the_case_table_holds_them_equal(LatticeStringEncodingEnum encoding, string pairName)
    {
        var (left, right, equal) = Pairs[pairName];
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, SchemaIn(encoding));
        var accounts = database.Set<Account>();
        var records = database.Set("Account");

        await accounts.InsertAsync(new Account { Login = left, Name = "left" });

        // A read under the other text, through the class and by name, finds the entity with the
        // spelling it was created with, or nothing.
        Assert.Equal(equal ? left : null, accounts.Get(right)?.Login);
        Assert.Equal(equal ? left : null, records.Get(right)?["Login"]);

        // An insert under it is DuplicateKey, or another entity.
        var insert = () => records.InsertAsync(new LatticeRecord(records.EntityType) { ["Login"] = right, ["Name"] = "right" });

        if (equal)
        {
            await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, insert);
            Assert.Equal("left", Assert.Single(accounts.Enumerate()).Name);
        }
        else
        {
            await insert();
            Assert.Equal("right", accounts.Get(right)!.Name);
            Assert.Equal("left", accounts.Get(left)!.Name);
        }
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Write_batch_takes_one_operation_for_two_texts_exactly_when_the_case_table_holds_them_equal(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, Schema);

        foreach (var (left, right, equal) in Pairs.Values)
        {
            var batch = database.CreateWriteBatch();
            batch.Insert(new Account { Login = left, Name = "left" });

            if (equal)
            {
                Assert.Throws<ArgumentException>(() => batch.Delete("Account", right));
                Assert.Equal(1, batch.Count);
            }
            else
            {
                batch.Delete("Account", right);
                Assert.Equal(2, batch.Count);
            }
        }
    }

    [Fact]
    public void Map_keys_and_Set_members_compared_without_case_are_one_exactly_when_the_case_table_holds_them_equal()
    {
        Assert.True(Schema.TryGetEntityType("Holder", out var holder));

        foreach (var (left, right, equal) in Pairs.Values)
        {
            var record = new LatticeRecord(holder);
            var options = new Dictionary<string, int>(StringComparer.Ordinal) { [left] = 1, [right] = 2 };

            // A Map that gives one key twice is refused; a Set keeps the first of equal members.
            if (equal)
            {
                Assert.Throws<ArgumentException>(() => record["Options"] = options);
            }
            else
            {
                record["Options"] = options;
                Assert.Equal(2, record.Get<Dictionary<string, int>>("Options")!.Count);
            }

            record["Rights"] = new[] { left, right };
            var rights = record.Get<HashSet<string>>("Rights")!;

            if (equal)
            {
                Assert.Equal(left, Assert.Single(rights));
            }
            else
            {
                Assert.Equal(2, rights.Count);
            }
        }
    }

    private static LatticeSchema SchemaIn(LatticeStringEncodingEnum encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Account", "ClrType": "{{typeof(Account).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Login", "Type": "String", "Comparison": "OrdinalIgnoreCase" }, { "Id": 2, "Name": "Name", "Type": "String" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Holder",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Options", "Type": "Map", "Key": { "Type": "String", "Comparison": "OrdinalIgnoreCase" }, "Value": { "Type": "Int32" } },
                { "Id": 3, "Name": "Rights", "Type": "Set", "Element": { "Type": "String", "Comparison": "OrdinalIgnoreCase" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Account
    {
        public string Login { get; set; } = "";

        public string Name { get; set; } = "";
    }
}
