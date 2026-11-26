using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class ListCollectionMatrixProfile<T>(string profile, string element, Func<int, int, T> item)
    : CollectionMatrixProfile(profile, LatticeFieldTypeEnum.List, $"\"Element\": {element}")
{
    public override object? Values(int count, int salt) => count < 0
        ? null
        : Enumerable.Range(0, count).Select(index => item(index, salt)).ToList();

    public override void AssertOwned(object? expected, LatticeRecord record) =>
        Assert.Equal((List<T>?)expected, record.Get<List<T>>("Values"));

    public override LatticeCommandResult[] Edit(LatticeUpdate update, int count, int salt, int predecessorSalt) => count > 0
        ? [update.ListReplace("Values", count - 1, item(count, salt))]
        : [];

    public override object? AfterEdit(int count, int oldSalt, int newSalt)
    {
        var expected = (List<T>?)Values(count, oldSalt);
        if (count > 0)
        {
            expected![^1] = item(count, newSalt);
        }

        return expected;
    }

    public override void AssertView(LatticeValueView field, object? expected, ViewerPause? pause)
    {
        if (AssertNullField(field, expected, pause))
        {
            return;
        }

        var items = (List<T>)expected!;
        var list = field.List;
        Assert.Equal(items.Count, list.Count);
        var iterator = list.GetEnumerator();
        var hasFirst = iterator.MoveNext();
        Assert.Equal(items.Count > 0, hasFirst);
        var copy = iterator;
        var held = hasFirst ? iterator.Current : default;

        pause?.Hold();

        if (!hasFirst)
        {
            Assert.False(copy.MoveNext());
            return;
        }

        Assert.Equal(items[0], Read<T>(held));
        Assert.Equal(items[0], Read<T>(copy.Current));
        Assert.Equal(items[0], Read<T>(iterator.Current));
        for (var index = 1; index < items.Count; index++)
        {
            Assert.True(iterator.MoveNext());
            Assert.Equal(items[index], Read<T>(iterator.Current));
            Assert.Equal(items[index], Read<T>(list[index]));
            if (pause is not null)
            {
                Assert.True(copy.MoveNext());
                Assert.Equal(items[index], Read<T>(copy.Current));
            }
        }

        Assert.False(iterator.MoveNext());
        if (pause is not null)
        {
            Assert.False(copy.MoveNext());
            return;
        }

        Assert.Equal(items.Count > 1, copy.MoveNext());
        if (items.Count > 1)
        {
            Assert.Equal(items[1], Read<T>(copy.Current));
        }
    }
}
