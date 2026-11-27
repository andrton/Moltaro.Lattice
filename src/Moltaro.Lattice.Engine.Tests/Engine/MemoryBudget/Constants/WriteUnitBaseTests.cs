using System.Diagnostics;
using System.Reflection;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Measurements;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Constants;

// The base of a write unit (BatchInputCharge.PerUnitBytes) covers what a unit of one member costs beyond
// PerMemberBytes across both nodes: a child transaction, a single write a child forwards and a batch of one
// member a child forwards. Each is measured as the allocation of the whole process, master and child in it,
// per unit, as the difference of two runs in which every unit is applied by the child before the next is
// submitted:
// - a child transaction: writes on a master whose child follows, less the same writes on a master alone;
// - a forwarded write: the writes submitted on the child, less the same writes on the master with its child;
// - a forwarded batch: batches of one member built and committed on the child, less the same batches on the
//   master with its child.
// A write on a child returns once the child has applied it; on the master the test spins, allocating
// nothing, until the child has. Each write is a whole replace of a User of the reference fixture that changes
// it. The order of the runs turns with each window, and each figure is the median of Windows windows. The
// figures belong to the optimized build that ships, so the test runs only in one.
[Collection<WholeProcessAllocationCollection>]
public sealed class WriteUnitBaseTests
{
    private const int Users = 32;

    private const int Units = 2_048;

    private const int Warmups = 2;

    private const int Windows = 5;

    // The two versions of each user, so that every replace changes the user.
    private readonly List<LatticeRecord>[] _users = new List<LatticeRecord>[2];

    private long _writes;

    // Whether the engine is an optimized build, the build that ships and whose figures the constants state.
    public static bool Optimized => typeof(EngineDatabase).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    [Fact(Skip = "The constants state figures of the optimized build that ships; a Debug build allocates more on its asynchronous paths.",
        SkipUnless = nameof(Optimized))]
    public async Task A_child_transaction_a_forwarded_write_and_a_forwarded_batch_of_one_member_cost_at_most_the_base_of_a_unit_and_one_member()
    {
        var schema = LatticeSchema.Compile(ReferenceFixture.SchemaJson);
        _users[0] = Generate(schema);
        _users[1] = Generate(schema);
        for (var user = 0; user < Users; user++)
        {
            _users[1][user]["DisplayName"] = $"Other {user:D8}";
        }

        await using var alone = await EngineMaster.OpenAsync(schema);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        await LoadAsync(alone);
        await LoadAsync(master);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        await child.WaitForPositionAsync(master.Position, Bound);

        // The records of the child are made with the schema it received from its master.
        var childUsers = new[] { Generate(child.Schema), Generate(child.Schema) };
        for (var user = 0; user < Users; user++)
        {
            childUsers[1][user]["DisplayName"] = $"Other {user:D8}";
        }

        var runs = new (string Name, Func<Task<double>> Run)[]
        {
            ("alone", () => RunAsync(alone, _users, follower: null, batched: false)),
            ("child", () => RunAsync(master, _users, child, batched: false)),
            ("forwarded", () => RunAsync(child, childUsers, follower: null, batched: false)),
            ("batch child", () => RunAsync(master, _users, child, batched: true)),
            ("batch forwarded", () => RunAsync(child, childUsers, follower: null, batched: true)),
        };
        var figures = runs.ToDictionary(run => run.Name, _ => new List<double>());
        for (var window = 0; window < Warmups + Windows; window++)
        {
            for (var turn = 0; turn < runs.Length; turn++)
            {
                var (name, run) = runs[(turn + window) % runs.Length];
                var figure = await run();
                if (window >= Warmups)
                {
                    figures[name].Add(figure);
                }
            }
        }

        ExpectCovered("A child transaction of one effect", figures["child"], figures["alone"]);
        ExpectCovered("A write forwarded by a child", figures["forwarded"], figures["child"]);
        ExpectCovered("A batch of one member forwarded by a child", figures["batch forwarded"], figures["batch child"]);
    }

    // The median difference of the windows is within the base of a unit and one member.
    private static void ExpectCovered(string unit, List<double> measured, List<double> without)
    {
        var differences = measured.Zip(without, (first, second) => first - second).Order().ToList();
        var median = differences[differences.Count / 2];
        var covered = BatchInputCharge.PerUnitBytes + BatchInputCharge.PerMemberBytes;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{unit} costs {median:F0} bytes across both nodes (from {differences[0]:F0} to {differences[^1]:F0}); the base of a unit and one member are {covered}.");
        Assert.True(median <= covered, $"{unit} costs {median:F0} bytes across both nodes; the base of a unit and one member are {covered}.");
    }

    // The allocation of the whole process per unit while Units writes, or batches of one member, are made on
    // the node, each applied by the follower, when one is given, before the next.
    private async Task<double> RunAsync(ILatticeDatabase node, List<LatticeRecord>[] users, ILatticeDatabase? follower, bool batched)
    {
        var set = node.Set("User");
        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var unit = 0; unit < Units; unit++)
        {
            var user = Next(users);
            if (batched)
            {
                var batch = node.CreateWriteBatch();
                batch.Replace(user);
                await batch.CommitAsync();
            }
            else
            {
                await set.ReplaceAsync(user);
            }

            if (follower is not null)
            {
                SpinUntilApplied(follower, node.Position);
            }
        }

        var after = GC.GetTotalAllocatedBytes(precise: true);
        return (after - before) / (double)Units;
    }

    // The next replace: the users in turn, each in the version it does not hold.
    private LatticeRecord Next(List<LatticeRecord>[] users)
    {
        var write = _writes++;
        return users[write / Users % 2 == 0 ? 1 : 0][(int)(write % Users)];
    }

    // Waits without allocating, within the bound of the tests, until the follower has applied the position.
    private static void SpinUntilApplied(ILatticeDatabase follower, LatticePosition position)
    {
        var start = Stopwatch.GetTimestamp();
        while (!follower.Position.Equals(position))
        {
            if (Stopwatch.GetElapsedTime(start) >= Bound)
            {
                Assert.Fail($"The child did not reach the position {position} within the bound.");
            }

            Thread.SpinWait(32);
        }
    }

    private async Task LoadAsync(ILatticeDatabase master)
    {
        var batch = master.CreateWriteBatch();
        foreach (var user in _users[0])
        {
            batch.Insert(user);
        }

        await batch.CommitAsync();
    }

    private static List<LatticeRecord> Generate(LatticeSchema schema) =>
        ReferenceFixture.Generate(schema).Where(record => record.EntityType.Name == "User").Take(Users).ToList();
}
