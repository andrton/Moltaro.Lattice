using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class SetCollectionMatrixProfile<T>(string profile, string element, Func<int, int, T> item)
    : CollectionMatrixProfile(profile, LatticeFieldTypeEnum.Set, $"\"Element\": {element}")
{
    public override object? Values(int count, int salt) => count < 0
        ? null
        : Enumerable.Range(0, count).Select(index => item(index, salt)).ToHashSet();

    public override void AssertOwned(object? expected, LatticeRecord record)
    {
        var set = record.Get<HashSet<T>>("Values");
        if (expected is HashSet<T> items)
        {
            Assert.NotNull(set);
            Assert.True(items.SetEquals(set));
        }
        else
        {
            Assert.Null(set);
        }
    }

    public override LatticeCommandResult[] Edit(LatticeUpdate update, int count, int salt)
    {
        if (count <= 0)
        {
            return [];
        }

        return
        [
            update.SetRemove("Values", item(count - 1, salt - 1)),
            update.SetAdd("Values", item(count + 1, salt)),
        ];
    }

    public override LatticeCommandResult Remove(LatticeUpdate update, int ordinal, int salt) =>
        update.SetRemove("Values", item(ordinal, salt));

    public override object? AfterEdit(int count, int oldSalt, int newSalt)
    {
        var expected = (HashSet<T>?)Values(count, oldSalt);
        if (count > 0)
        {
            Assert.True(expected!.Remove(item(count - 1, oldSalt)));
            Assert.True(expected.Add(item(count + 1, newSalt)));
        }

        return expected;
    }

    public override void AssertView(LatticeValueView field, object? expected, ViewerPause? pause)
    {
        if (AssertNullField(field, expected, pause))
        {
            return;
        }

        var items = (HashSet<T>)expected!;
        var set = field.Set;
        Assert.Equal(items.Count, set.Count);
        var iterator = set.GetEnumerator();
        var hasFirst = iterator.MoveNext();
        Assert.Equal(items.Count > 0, hasFirst);
        var copy = iterator;
        var held = hasFirst ? iterator.Current : default;

        pause?.Hold();

        Assert.False(set.Contains(item(-1, -1)));
        var remaining = new HashSet<T>(items);
        if (!hasFirst)
        {
            Assert.False(copy.MoveNext());
            return;
        }

        Assert.Contains(Read<T>(held), items);
        Assert.Equal(Read<T>(iterator.Current), Read<T>(copy.Current));
        do
        {
            var member = Read<T>(iterator.Current);
            Assert.True(remaining.Remove(member));
            Assert.True(set.Contains(member));
        }
        while (iterator.MoveNext());

        Assert.Empty(remaining);
        Assert.Equal(items.Count > 1, copy.MoveNext());
        if (items.Count > 1)
        {
            Assert.Contains(Read<T>(copy.Current), items);
        }
    }
}
