namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal static class Verification
{
    public static void RunAll()
    {
        RunForKey<long>();
        RunForKey<Guid>();
        RunForKey<string>();
    }

    private static void RunForKey<TKey>() where TKey : notnull, IEquatable<TKey>
    {
        foreach (var count in new[] { 0, 1, 15, 16, 17, 127, 128, 129, 4097 })
        {
            var data = new KeyData<TKey>(count);
            Run<BlitzIndex<TKey>, TKey>(data);
            Run<DenseIndex<TKey>, TKey>(data);
            Run<ArcSlimIndex<TKey>, TKey>(data);
            Run<ArcIndex<TKey>, TKey>(data);
        }
    }

    private static void Run<TIndex, TKey>(KeyData<TKey> data)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        var map = TIndex.Create(16);
        for (var index = 0; index < data.Count; index++)
        {
            map.Insert(data.Keys[index], KeyData<TKey>.Value(index));
        }

        CheckState<TIndex, TKey>(ref map, data, data.Count, 0);
        foreach (var index in data.Order)
        {
            map.Set(data.EqualKeys[index], KeyData<TKey>.Value(index, 7));
        }

        CheckState<TIndex, TKey>(ref map, data, data.Count, 7);
        CheckChurn<TIndex, TKey>(ref map, data, 7);
        foreach (var key in data.EqualKeys)
        {
            if (!map.Remove(key) || map.Remove(key))
            {
                throw new InvalidOperationException($"{typeof(TIndex).Name}: inconsistent removal result.");
            }
        }

        CheckState<TIndex, TKey>(ref map, data, 0, 0);
    }

    public static void CheckState<TIndex, TKey>(
        ref TIndex map, KeyData<TKey> data, int expectedCount, int version)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        if (map.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"{typeof(TIndex).Name}: count {map.Count}, expected {expectedCount}.");
        }

        for (var index = 0; index < data.Count; index++)
        {
            var present = map.TryGet(data.EqualKeys[index], out var value);
            var expectedPresent = index < expectedCount;
            if (present != expectedPresent ||
                (present && value != KeyData<TKey>.Value(index, version)) ||
                map.TryGet(data.MissingKeys[index], out _))
            {
                throw new InvalidOperationException(
                    $"{typeof(TIndex).Name}: incorrect value or presence at input {index}, version {version}.");
            }
        }
    }

    public static void CheckChurn<TIndex, TKey>(ref TIndex map, KeyData<TKey> data, int version)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        for (var index = 0; index < data.Count; index += 4)
        {
            if (!map.Remove(data.EqualKeys[index]) || map.TryGet(data.Keys[index], out _))
            {
                throw new InvalidOperationException($"{typeof(TIndex).Name}: removal failed.");
            }
        }

        var expectedCount = data.Count - (data.Count + 3) / 4;
        if (map.Count != expectedCount)
        {
            throw new InvalidOperationException($"{typeof(TIndex).Name}: incorrect count after deletion.");
        }

        for (var index = 0; index < data.Count; index += 4)
        {
            map.Insert(data.Keys[index], KeyData<TKey>.Value(index, version));
        }

        CheckState<TIndex, TKey>(ref map, data, data.Count, version);
    }
}
