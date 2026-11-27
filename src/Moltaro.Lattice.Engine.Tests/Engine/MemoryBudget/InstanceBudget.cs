using System.Reflection;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Concurrency;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

internal static class InstanceBudget
{
    public const int Segment = 4 * 1024 * 1024;
    public const long OneSegmentWithReceive = 2L * Segment - 256 * 1024;

    public static EngineDatabase Master(long limit, CountingNativeAllocator allocator, int? rootPageLimit = null, bool listen = false,
        LatticeSchema? schema = null, IPersistence? persistence = null) =>
        EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = schema ?? PieceSchema.Schema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            ListenUri = listen ? MasterAndChild.Loopback : null,
            MemoryLimit = limit,
        }, new EngineTestInputs
        {
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            IndexSeed = 0,
            RootPageLimit = rootPageLimit,
            Persistence = persistence,
        });

    public static async Task<EngineDatabase> OpenAsync(long limit, CountingNativeAllocator allocator, int? rootPageLimit = null,
        bool listen = false, LatticeSchema? schema = null, IPersistence? persistence = null)
    {
        var node = Master(limit, allocator, rootPageLimit, listen, schema, persistence);
        try
        {
            await node.OpenAsync().AsTask().WaitAsync(Bound);
            return node;
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    public static async Task<LatticeException> RefusedAsync(Task operation, LatticeLimitEnum limit)
    {
        var failure = await Assert.ThrowsAsync<LatticeException>(() => operation.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.WriteRejected, failure.Error);
        Assert.Equal(limit, failure.Limit);
        Assert.Null(failure.OperationIndex);
        return failure;
    }

    public static Task<(long Id, int Count)> HoldPieceView(EngineDatabase node, ViewerPause pause) =>
        OwnThread.Run(() =>
        {
            Assert.True(node.Set("Piece").TryView(1L, view =>
            {
                var original = (view["Id"].Get<long>(), view["Count"].Get<int>());
                pause.Hold();
                Assert.Equal(original, (view["Id"].Get<long>(), view["Count"].Get<int>()));
                return original;
            }, out var value));
            return value;
        });

    public static T Backing<T>(object owner, string field) =>
        (T)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;
}
