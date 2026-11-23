using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Concurrency;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Revisions;

// The revision a read by key returns while batches are published, on hardware that orders stores weakly: a
// replace stores the number of its member after the mark of the stamp, by a release, so that a read never
// pairs the stamp and the root of one state with the member of the next, a revision no write produced
// (docs/design/arena-protocol.md, "The stamp of a root entry"). x64 keeps stores in their order, so no run on
// it fails for the order of that store; the test is for a machine of many ARM64 cores, and runs only in the
// lane of the ordering stress, with LATTICE_ORDERING_STRESS=1.
//
// Batches replace every item, one batch after another, each giving the items its version and adding them in
// another order, so that the number of the member of an item changes with every batch, while readers on every
// processor but one read the items by key. Every revision read is the one the batch of the version copied gave
// that item: the history with the number of its member in that batch, and the sequence of that batch
// (MemberRevisionTests).
public sealed class MemberRevisionUnderReadsTests
{
    private const int Items = 8;

    private const int Batches = 100_000;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Versioned", "ClrType": "{{typeof(Versioned).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Version", "Type": "Int64" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static bool LaneEnabled => Environment.GetEnvironmentVariable("LATTICE_ORDERING_STRESS") == "1";

    [Fact(Skip = "The lane of the ordering stress is not enabled; LATTICE_ORDERING_STRESS=1 enables it.", SkipUnless = nameof(LaneEnabled))]
    public async Task Read_by_key_beside_batches_returns_the_revision_the_batch_of_the_state_it_copied_gave_the_entity()
    {
        await using var master = await EngineMaster.OpenAsync(Schema);
        var items = master.Set<Versioned>();
        var first = PositionSequence.Of((await BatchOf(master, version: 0).CommitAsync()).Position);
        var history = ulong.Parse(master.Position.ToString().Split('-')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        await ReadersAndWriter.RunAsync(
            Math.Max(2, Environment.ProcessorCount - 1),
            reader =>
            {
                var random = new Random(reader);

                return () =>
                {
                    var id = random.NextInt64(1, Items + 1);

                    Assert.True(items.TryGet(id, out var item, out var revision));

                    var given = EntityTable.RevisionOf(history, first + (ulong)item.Version, MemberOf(id, item.Version));

                    if (revision != given)
                    {
                        Assert.Fail($"Item {id} in version {item.Version} was read with the revision {revision}; its batch gave it {given}.");
                    }
                };
            },
            async () =>
            {
                for (var version = 1L; version <= Batches; version++)
                {
                    var committed = await BatchOf(master, version).CommitAsync();

                    Assert.Equal(first + (ulong)version, PositionSequence.Of(committed.Position));
                }
            });
    }

    // The batch of a version: every item, inserted by version 0 and replaced by every later one, the first
    // added at the place the version turns to.
    private static ILatticeWriteBatch BatchOf(ILatticeDatabase database, long version)
    {
        var batch = database.CreateWriteBatch();

        for (var place = 0L; place < Items; place++)
        {
            var item = new Versioned { Id = 1 + ((place + version) % Items), Version = version };

            if (version == 0)
            {
                batch.Insert(item);
            }
            else
            {
                batch.Replace(item);
            }
        }

        return batch;
    }

    // The number of the member of the item in the batch of the version: its place in that batch, from 1.
    private static uint MemberOf(long id, long version) => (uint)((((id - 1 - version) % Items) + Items) % Items) + 1;

    internal sealed class Versioned
    {
        public long Id { get; set; }

        public long Version { get; set; }
    }
}
