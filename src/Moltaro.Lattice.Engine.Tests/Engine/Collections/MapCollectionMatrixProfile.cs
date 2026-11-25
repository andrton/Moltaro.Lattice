using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class MapCollectionMatrixProfile<TKey, TValue>(string profile, string key, string value, Func<int, int, TKey> keyAt, Func<int, int, TValue> valueAt)
    : CollectionMatrixProfile(profile, LatticeFieldTypeEnum.Map, $"\"Key\": {key}, \"Value\": {value}")
    where TKey : notnull
{
    public override object? Values(int count, int salt) => count < 0
        ? null
        : Enumerable.Range(0, count).ToDictionary(index => keyAt(index, salt), index => valueAt(index, salt));

    public override void AssertOwned(object? expected, LatticeRecord record)
    {
        var map = record.Get<Dictionary<TKey, TValue>>("Values");
        if (expected is not Dictionary<TKey, TValue> items)
        {
            Assert.Null(map);
            return;
        }

        Assert.NotNull(map);
        Assert.Equal(items.Count, map.Count);
        foreach (var (member, value) in items)
        {
            Assert.True(map.TryGetValue(member, out var found));
            Assert.Equal(value, found);
        }
    }

    public override LatticeCommandResult[] Edit(LatticeUpdate update, int count, int salt) => count > 0
        ? [update.MapSet("Values", keyAt(count - 1, salt - 1), valueAt(count, salt))]
        : [];

    public override LatticeCommandResult Remove(LatticeUpdate update, int ordinal, int salt) =>
        update.MapRemove("Values", keyAt(ordinal, salt));

    public override object? AfterEdit(int count, int oldSalt, int newSalt)
    {
        var expected = (Dictionary<TKey, TValue>?)Values(count, oldSalt);
        if (count > 0)
        {
            expected![keyAt(count - 1, oldSalt)] = valueAt(count, newSalt);
        }

        return expected;
    }

    public override void AssertView(LatticeValueView field, object? expected, ViewerPause? pause)
    {
        if (AssertNullField(field, expected, pause))
        {
            return;
        }

        var items = (Dictionary<TKey, TValue>)expected!;
        var map = field.Map;
        Assert.Equal(items.Count, map.Count);
        var iterator = map.GetEnumerator();
        var hasFirst = iterator.MoveNext();
        Assert.Equal(items.Count > 0, hasFirst);
        var copy = iterator;
        var held = hasFirst ? iterator.Current : default;

        pause?.Hold();

        var missed = keyAt(-1, -1);
        Assert.False(map.TryGetValue(missed, out _));
        var remaining = new Dictionary<TKey, TValue>(items);
        if (!hasFirst)
        {
            Assert.False(copy.MoveNext());
            return;
        }

        Assert.Equal(items[Read<TKey>(held.Key)], Read<TValue>(held.Value));
        Assert.Equal(Read<TKey>(iterator.Current.Key), Read<TKey>(copy.Current.Key));
        bool more;
        do
        {
            var entry = iterator.Current;
            var member = Read<TKey>(entry.Key);
            Assert.True(remaining.Remove(member, out var value));
            Assert.Equal(value, Read<TValue>(entry.Value));
            Assert.True(map.TryGetValue(member, out var found));
            Assert.Equal(value, Read<TValue>(found));
            if (pause is not null)
            {
                var copyEntry = copy.Current;
                Assert.Equal(member, Read<TKey>(copyEntry.Key));
                Assert.Equal(value, Read<TValue>(copyEntry.Value));
            }

            more = iterator.MoveNext();
            if (pause is not null)
            {
                Assert.Equal(more, copy.MoveNext());
            }
        }
        while (more);

        Assert.Empty(remaining);
        if (pause is not null)
        {
            return;
        }

        Assert.Equal(items.Count > 1, copy.MoveNext());
        if (items.Count > 1)
        {
            Assert.Contains(Read<TKey>(copy.Current.Key), items.Keys);
        }
    }
}
