using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// An ownership audit after churn of entities whose indexes cover fields inside owned values finds every block
// owned by exactly one live entity, by an index or by its pool, and every reference to a text entry held by
// a root or by a node (OwnershipAudit): an index over a String inside an owned value stores a text longer than
// 64 bytes in an entry of the text table, which the entries and separators of its nodes share with their
// roots, as an index over a field of the entity does. The writes are inserts, replaces, upserts and deletes
// and write batches over a few keys, drawn from a generator with a fixed seed, so a failure repeats, among
// them writes that fail and leave nothing behind:
// - refused for memory: an arena of three segments of 4 MiB refuses a member whose Work holds about 6 MB of
//   lines while another such member is held, and some batches of them whole;
// - refused by their condition: an insert of a key that exists, a replace of a key that does not;
// - refused for a value of the unique index over a path that another entity holds;
// - a write batch refused whole for one failing member.
// The owned value on the paths appears and disappears, its texts are short, of 64 and 65 bytes and above a
// chunk, and shared between entities. The audit runs after every round.
//
// Member: Id, an Int64; Name, a String; Home, a nullable owned Address; Work, an owned Address; Places, a
// List of Addresses. An Address holds City, a String, Zip, an Int32, and Lines, a List of String. The
// indexes: ByName over Name, ByHomeCity over Home.City, ByWorkCity over Work.City, and ByHomeZip, unique,
// over Home.Zip.
public sealed class OwnershipOfIndexesAfterChurnTests
{
    private const int Rounds = 30;

    private const int OperationsPerRound = 12;

    private const int Keys = 8;

    private const int Shapes = 5;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Zip", "Type": "Int32" },
                { "Id": 3, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Member",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Name", "Type": "String" },
                { "Id": 3, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 4, "Name": "Work", "Type": "Owned", "OwnedType": "Address" },
                { "Id": 5, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByName", "Fields": [2] },
                { "Id": 2, "Name": "ByHomeCity", "Fields": [ [3, 1] ] },
                { "Id": 3, "Name": "ByWorkCity", "Fields": [ [4, 1] ] },
                { "Id": 4, "Name": "ByHomeZip", "Fields": [ [3, 2] ], "Unique": true }
              ]
            }
          ]
        }
        """);

    [Fact]
    public async Task Ownership_audit_after_churn_of_entities_whose_indexes_cover_fields_inside_owned_values_accounts_for_every_block_and_every_text_reference()
    {
        var database = await EngineMaster.OpenAsync(Schema, new EngineTestInputs
        {
            // Let each builder snapshot the generated roots. The native topology retains
            // the three-grant pressure that exercises both single and batch rollback.
            Arena = new ArenaOptions { MemoryLimit = 64L * 1024 * 1024, NativeCapacityLimit = 3L * 4 * 1024 * 1024 },
        });
        var random = new Random(18);
        var outcomes = new Dictionary<string, int>();

        try
        {
            var members = database.Set("Member");

            for (var round = 0; round < Rounds; round++)
            {
                for (var operation = 0; operation < OperationsPerRound; operation++)
                {
                    var outcome = await OperateAsync(database, members, random, (round * OperationsPerRound) + operation);

                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                }

                OwnershipAudit.Expect(database);
            }

            // Every way a write ends was met.
            foreach (var outcome in new[] { "written", "deleted", "Memory", "DuplicateKey", "NotFound", "UniqueConstraintViolation", "batch written", "batch Memory", "batch refused" })
            {
                Assert.True(outcomes.GetValueOrDefault(outcome) > 0, $"No operation ended as {outcome}: {string.Join(", ", outcomes)}.");
            }

            // And the last state still holds what a search through a path finds.
            Assert.Equal(
                members.Enumerate().Count(member => member["Home"] is null),
                members.Index("ByHomeCity").Find(IndexKey.Equal<string?>(null)).Count());
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // One operation drawn from the generator, and how it ended.
    private static async Task<string> OperateAsync(EngineDatabase database, ILatticeSet members, Random random, int number)
    {
        var key = (long)random.Next(Keys);
        var shape = random.Next(Shapes);

        try
        {
            switch (random.Next(8))
            {
                case 0:
                    await members.InsertAsync(MemberOf(members, key, shape, number));
                    return "written";
                case 1:
                    await members.ReplaceAsync(MemberOf(members, key, shape, number));
                    return "written";
                case 2 or 3:
                    await members.UpsertAsync(MemberOf(members, key, shape, number));
                    return "written";
                case 4:
                    return (await members.DeleteAsync(key)).Changed ? "deleted" : "nothing to delete";
                case 5:
                    return await BatchAsync(database, members, random, number, failing: false);
                default:
                    return await BatchAsync(database, members, random, number, failing: random.Next(2) == 0);
            }
        }
        catch (LatticeException exception)
        {
            return exception.Limit is { } limit ? limit.ToString() : exception.Error.ToString();
        }
    }

    // A batch of two to four upserts of other keys, or of the same keys with an insert of a key that exists
    // as its failing member.
    private static async Task<string> BatchAsync(EngineDatabase database, ILatticeSet members, Random random, int number, bool failing)
    {
        var batch = database.CreateWriteBatch();
        var keys = Enumerable.Range(0, Keys).Select(key => (long)key).OrderBy(_ => random.Next()).Take(random.Next(2, 5)).ToArray();

        foreach (var key in keys)
        {
            batch.Upsert(MemberOf(members, key, random.Next(Shapes), number));
        }

        if (failing)
        {
            await members.UpsertAsync(MemberOf(members, keys[0] + Keys, 0, number));
            batch.Insert(MemberOf(members, keys[0] + Keys, 0, number));
        }

        try
        {
            await batch.CommitAsync();

            return "batch written";
        }
        catch (LatticeException exception)
        {
            // A batch refused for memory names the limit; one refused for its failing member does not.
            return exception.Limit is { } limit ? $"batch {limit}" : "batch refused";
        }
    }

    // A member with the key, of the shape, its values made from the number: 0 without Home, its Work in a
    // short city; 1 a Home in one of three cities of 100 bytes, which entities share, and a Work city of 64
    // bytes; 2 a Home city of 65 bytes and 300 Places, each with a line; 3 a Work of about 6 MB of lines; 4 a
    // Home city above a chunk, shared as well. The Zip of Home is the number less a few, so that two entities
    // now and then take the same one.
    private static LatticeRecord MemberOf(ILatticeSet members, long id, int shape, int number)
    {
        Assert.True(Schema.TryGetOwnedType("Address", out var address));

        LatticeOwnedValue AddressOf(string city, int zip, string[] lines) => new(address) { ["City"] = city, ["Zip"] = zip, ["Lines"] = lines };

        var record = new LatticeRecord(members.EntityType)
        {
            ["Id"] = id,
            ["Name"] = number % 2 == 0 ? $"name {number % 5}" : new string('n', 70 + (number % 3)),
            ["Work"] = AddressOf("work", number, []),
            ["Places"] = Array.Empty<LatticeOwnedValue>(),
        };
        var zip = number / 4;

        switch (shape)
        {
            case 0:
                break;

            case 1:
                record["Home"] = AddressOf(new string((char)('a' + (number % 3)), 100), zip, ["home"]);
                record["Work"] = AddressOf(new string('w', 64), number, []);
                break;

            case 2:
                record["Home"] = AddressOf(new string('h', 65), zip, []);
                record["Places"] = Enumerable.Range(0, 300).Select(index => AddressOf($"place {index}", index, [$"line {number}"])).ToArray();
                break;

            case 3:
                record["Work"] = AddressOf("far", number, [.. Enumerable.Repeat(new string('l', 70_000), 90)]);
                break;

            default:
                record["Home"] = AddressOf(new string('c', 70_000 + (number % 2)), zip, []);
                break;
        }

        return record;
    }
}
