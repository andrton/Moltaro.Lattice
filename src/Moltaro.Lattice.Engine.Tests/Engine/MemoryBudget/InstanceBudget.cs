using System.Buffers;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

internal static class InstanceBudget
{
    public const string Pending = "The instance memory budget is not implemented yet.";
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

    public static BudgetCapacitySample ExpectCapacity(EngineDatabase node, CountingNativeAllocator allocator) =>
        ExpectCapacity(node, allocator, out _);

    public static BudgetCapacitySample ExpectCapacity(EngineDatabase node, CountingNativeAllocator allocator,
        out long independentlyOwned, params object[] backings)
    {
        var managed = 0L;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var sample = node.InspectBudget((owner, backing) =>
        {
            Assert.True(visited.Add(backing), $"Two owners reported the same backing ({owner}).");
            managed += ManagedObjectSize(backing);
        });

        Assert.True(managed > 0);
        foreach (var required in backings)
        {
            Assert.Contains(required, visited);
        }

        Assert.Equal(allocator.OutstandingBytes, sample.NativeBytes);
        independentlyOwned = allocator.OutstandingBytes + managed;
        Assert.Equal(independentlyOwned, sample.ChargedBytes);
        Assert.InRange(sample.PeakBytes, sample.ChargedBytes, node.Counters.MemoryLimit);
        return sample;
    }

    public static async Task<LatticeException> RefusedAsync(Task operation, LatticeLimitEnum limit)
    {
        var failure = await Assert.ThrowsAsync<LatticeException>(() => operation.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.WriteRejected, failure.Error);
        Assert.Equal(limit, failure.Limit);
        Assert.Null(failure.OperationIndex);
        return failure;
    }

    // The tests run on the .NET 10 64-bit hosts. Size the actual arrays by lengths and element
    // widths, independently of the reservation. Typed allocation gives the size of an owned
    // stand-alone object without relying on the budget's arithmetic.
    public static T Backing<T>(object owner, string field) =>
        (T)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(owner)!;

    public static Array[] DictionaryArrays(object owner) =>
        [.. new[] { Backing<Array?>(owner, "_buckets"), Backing<Array?>(owner, "_entries") }
            .OfType<Array>().Where(array => array.Length != 0)];

    public static byte[][] BatchPieceArrays(ILatticeWriteBatch batch)
    {
        var operations = Backing<IList>(batch, "_operations");
        var operation = Assert.IsAssignableFrom<object>(Assert.Single(operations));
        var root = Assert.IsType<ReadOnlySequence<byte>>(operation.GetType().GetProperty("Root")!.GetValue(operation));
        var allocations = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var segment in root)
        {
            Assert.True(MemoryMarshal.TryGetArray(segment, out var part));
            allocations.Add(part.Array!);
        }

        return [.. allocations];
    }

    public static long ManagedObjectSize(object backing)
    {
        if (backing is Array array)
        {
            var element = array.GetType().GetElementType()!;
            var width = element.IsValueType ? SizeOf(element) : IntPtr.Size;
            return Align(3L * IntPtr.Size + array.LongLength * width);
        }

        return (long)typeof(InstanceBudget).GetMethod(nameof(AllocatedObjectSize), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(backing.GetType()).Invoke(null, null)!;
    }

    private static long Align(long bytes) => (bytes + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;

    private static int SizeOf(Type element) =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(element).Invoke(null, null)!;

    private static long AllocatedObjectSize<T>() where T : class
    {
        GC.KeepAlive(RuntimeHelpers.GetUninitializedObject(typeof(T)));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var value = RuntimeHelpers.GetUninitializedObject(typeof(T));
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(value);
        return bytes;
    }
}
