using System.Diagnostics;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Text;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// These observations run at phase boundaries under Writes, outside ordinary hits.
internal static class TextMetadataTestAccess
{
    public static TextCapacitySnapshot Capacity(EngineDatabase node) =>
        UnderWrites(node, static parts => parts.Arena.Texts.MeasureCapacity());

    public static TextEntrySnapshot? Entry(EngineDatabase node, uint id) =>
        UnderWrites<TextEntrySnapshot?>(node, parts => parts.Arena.Texts.TryMeasureEntry(id, out var entry) ? entry : null);

    public static int Maintain(EngineDatabase node, int limit)
    {
        var work = UnderWrites(node, parts => parts.Arena.Texts.Maintain(limit));
        Assert.InRange(work, 0, limit);
        return work;
    }

    public static int RetireOne(EngineDatabase node)
    {
        var work = UnderWrites(node, static parts => parts.Retired.DrainStep(1));
        Assert.InRange(work, 0, 1);
        return work;
    }

    public static T UnderWrites<T>(EngineDatabase node, Func<DatabaseParts, T> measure)
    {
        var deadline = Stopwatch.StartNew();
        var wait = new SpinWait();
        T result = default!;
        Action<DatabaseParts> sample = parts => result = measure(parts);
        while (!node.TryRunMaintenanceStep(sample))
        {
            Assert.True(deadline.Elapsed < Bound, "The engine did not admit the required phase.");
            wait.SpinOnce();
        }

        return result;
    }

    public static long References(EngineDatabase node, string text, string encoding)
    {
        var expected = TextTally.EncodingOf(encoding).GetBytes(text);
        var matches = node.TextReferences().Where(entry => entry.Text.AsSpan().SequenceEqual(expected)).ToArray();
        return matches.Length == 0 ? 0 : Assert.Single(matches).References;
    }

    public static Dictionary<long, uint> TextIds(EngineDatabase node, string entity, string field, IEnumerable<long> keys)
    {
        var expected = keys.ToHashSet();
        var actual = new Dictionary<long, uint>();
        node.ForEachLiveRoot((arena, layout, _, address) =>
        {
            if (layout.EntityType.Name != entity)
            {
                return;
            }

            var reader = new RootReader(arena, layout, address);
            var key = reader.GetInteger(layout.EntityType.Key);
            if (expected.Contains(key))
            {
                var descriptor = reader.ReadDescriptor(layout.StoredFieldOf(layout.EntityType.GetField(field)));
                Assert.Equal(DescriptorKindEnum.Text, descriptor.Kind);
                actual.Add(key, checked((uint)descriptor.Location));
            }
        });

        Assert.Equal(expected.Count, actual.Count);
        return actual;
    }

    public static void ExpectAccounting(EngineDatabase node, TextCapacitySnapshot snapshot)
    {
        var total = Bytes(snapshot.LocatorDirectory) + Bytes(snapshot.LocatorLeaves) + Bytes(snapshot.WriterMetadata) +
            Bytes(snapshot.ActiveBuckets) + Bytes(snapshot.OldBuckets) + Bytes(snapshot.RetiredLocator);
        Assert.Equal(total, node.Counters.TextTableBytes);
    }

    public static long Bytes(IReadOnlyList<TextAllocationShape> groups) =>
        groups.Sum(shape => shape.InstanceCount * (shape.ArrayElementBytes == 0
            ? Align(2L * IntPtr.Size + shape.ObjectFieldBytes)
            : Align(3L * IntPtr.Size + (long)shape.ArrayElementBytes * shape.ArrayLength)));

    private static long Align(long size) => (size + IntPtr.Size - 1) & -IntPtr.Size;
}
