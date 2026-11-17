using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// What a child applies of a change round-trips every write the engine has so far (brief of Epic 1,
// sprint S11, Acceptance: "the encoding of effects round-trips every write the engine has so far"; a
// Write carries the root in the load format, decided after the study, point 6): for every scalar type,
// in a UTF-8 and in a UTF-16 database, every value of every field of every entity the master inserts
// and then replaces after the child is Ready is the value the child reads by name, as it is stored
// (EveryTypeValues), with the extremes and the special values of each type and null in the nullable
// fields, out of line and in chunks as well.
public sealed class EveryTypeChangeTests
{
    private static readonly string[] EntityTypes = ["Item", "Twin"];

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public async Task Child_applies_every_value_of_every_scalar_type_as_its_master_writes_it(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(EveryType.Compile(encoding));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        foreach (var entityType in EntityTypes)
        {
            var records = master.Set(entityType);

            for (var number = 1; number <= EveryTypeValues.Entities; number++)
            {
                await records.InsertAsync(EveryTypeValues.RecordOf(records.EntityType, number));
            }
        }

        await Eventually.AtAsync(child, master.Position);
        ExpectSame(master, child);

        // Each entity replaced by the values of the next number: every field changes, null included.
        foreach (var entityType in EntityTypes)
        {
            var records = master.Set(entityType);

            for (var number = 1; number <= EveryTypeValues.Entities; number++)
            {
                var record = EveryTypeValues.RecordOf(records.EntityType, number + 1);

                record["Id"] = (long)number;
                await records.ReplaceAsync(record);
            }
        }

        await Eventually.AtAsync(child, master.Position);
        ExpectSame(master, child);
    }

    private static void ExpectSame(ILatticeDatabase master, ILatticeDatabase child)
    {
        foreach (var entityType in EntityTypes)
        {
            var onMaster = master.Set(entityType).Enumerate().OrderBy(record => record.Get<long>("Id")).ToList();
            var onChild = child.Set(entityType).Enumerate().OrderBy(record => record.Get<long>("Id")).ToList();

            Assert.Equal(onMaster.Select(record => record["Id"]), onChild.Select(record => record["Id"]));

            for (var index = 0; index < onMaster.Count; index++)
            {
                foreach (var field in onMaster[index].EntityType.Fields)
                {
                    Assert.True(
                        EveryTypeValues.Same(onMaster[index][field.Name], onChild[index][field.Name]),
                        $"{entityType} {onMaster[index]["Id"]}, field {field.Name}: the child read {onChild[index][field.Name] ?? "null"}, the master {onMaster[index][field.Name] ?? "null"}.");
                }
            }
        }
    }
}
