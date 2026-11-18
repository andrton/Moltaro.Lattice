using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.LoadingUnderWrites;

// Beside the interleavings that InterleavedWritesTests makes, a small soak: one writer runs a sequence of
// writes drawn from a random source with a fixed seed — inserts, replaces, upserts, deletes, deletes of a
// key the master does not hold, and deletes followed by inserts of the same key — over a small set of
// keys, while a child loads through a link that paces what the master sends (HeldLink.Paced), so that
// the load takes many times what one write takes, and goes on while the child follows. The sequence of
// writes is the same at every run; how it interleaves with the load is not. When the writer has ended,
// the child reaches the position of the master and holds what the master holds there, with the same
// revisions, and the master holds what the writer wrote (brief of Epic 1, sprint S11, Acceptance).
public sealed class LoadUnderWritesSoakTests
{
    private const int Seed = 20261117;

    private const int Keys = 300;

    private const int Writes = 3_000;

    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact]
    public async Task Child_that_loads_and_follows_while_one_writer_runs_a_random_sequence_of_writes_ends_equal_to_the_master()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var pieces = master.Set<PieceSchema.Piece>();
        var random = new Random(Seed);
        var expected = new Dictionary<long, int>();
        var version = 0;

        for (var id = 0L; id < Keys; id += 2)
        {
            version++;
            await pieces.InsertAsync(Pieces.Versioned(id, version, DataBytes(random)));
            expected[id] = version;
        }

        await using var link = HeldLink.Paced(master.ListenUri!, portion: 2 * 64 * 1024, pause: TimeSpan.FromMilliseconds(2));
        await using var child = (EngineDatabase)LatticeDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound));
        var opening = child.OpenAsync().AsTask();

        for (var write = 0; write < Writes; write++)
        {
            var id = (long)random.Next(Keys);
            var held = expected.ContainsKey(id);
            var piece = Pieces.Versioned(id, ++version, DataBytes(random));

            switch (random.Next(6))
            {
                case 0 when !held:
                    await pieces.InsertAsync(piece);
                    expected[id] = version;
                    break;

                case 1 when held:
                    await pieces.ReplaceAsync(piece);
                    expected[id] = version;
                    break;

                case 2:
                    await pieces.UpsertAsync(piece);
                    expected[id] = version;
                    break;

                case 3 when held:
                    await pieces.DeleteAsync(id);
                    expected.Remove(id);
                    break;

                case 4 when held:
                    await pieces.DeleteAsync(id);
                    await pieces.InsertAsync(piece);
                    expected[id] = version;
                    break;

                default:
                    await pieces.DeleteAsync(-1 - id);
                    break;
            }
        }

        await opening.WaitAsync(Bound);
        await Eventually.AtAsync(child, master.Position);

        Pieces.ExpectSameAt(master, child);

        var onMaster = pieces.Enumerate().ToDictionary(piece => piece.Id, piece => piece.Count);

        Assert.Equal(expected.OrderBy(pair => pair.Key), onMaster.OrderBy(pair => pair.Key));
        Assert.All(child.Set<PieceSchema.Piece>().Enumerate(), piece => Assert.True(Pieces.IsWhole(piece), $"Piece {piece.Id} on the child is not one written state."));
    }

    // Most pieces small, some of 64 KiB, so that the transactions of some writes take two frames.
    private static int DataBytes(Random random) => random.Next(10) == 0 ? 64 * 1024 : 32;
}
