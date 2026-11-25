using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class ReplicatedCollectionEqualityTests
{
    private const long Key = 17;
    private const uint FirstFloatNaN = 0x7FC0_0001;
    private const uint LaterFloatNaN = 0x7FC0_0002;
    private const ulong FirstDoubleNaN = 0x7FF8_0000_0000_0001;
    private const ulong LaterDoubleNaN = 0x7FF8_0000_0000_0002;

    private static readonly string FirstText = "MiXeD é𐐨 " + new string('a', 91);
    private static readonly string EquivalentText = "mIxEd É𐐨 " + new string('A', 91);

    [Theory]
    [InlineData("Utf8", false)]
    [InlineData("Utf16", false)]
    [InlineData("Utf8", true)]
    [InlineData("Utf16", true)]
    public async Task Equal_probes_preserve_the_first_representation_in_copies_and_held_views(
        string encoding, bool hashed)
    {
        var schema = LatticeSchema.Compile($$"""
            {
              "StringEncoding": "{{encoding}}",
              "EntityTypes": [{
                "Id": 1, "Name": "Item",
                "Fields": [
                  { "Id": 1, "Name": "Id", "Type": "Int64" },
                  { "Id": 2, "Name": "Label", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                  { "Id": 3, "Name": "Single", "Type": "Set", "Element": { "Type": "Float32" } },
                  { "Id": 4, "Name": "Double", "Type": "Set", "Element": { "Type": "Float64" } },
                  { "Id": 5, "Name": "Text", "Type": "Set", "Element": { "Type": "String", "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" } },
                  { "Id": 6, "Name": "Options", "Type": "Map", "Key": { "Type": "String", "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" }, "Value": { "Type": "Int32" } }
                ],
                "PrimaryKey": [1],
                "Indexes": [{ "Id": 1, "Name": "ByLabel", "Fields": [2] }]
              }]
            }
            """);
        var fillers = hashed ? Enumerable.Range(2, 9).ToArray() : [];
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, new EngineTestInputs { IndexSeed = 13 });
        var source = master.Set("Item");
        var options = fillers.ToDictionary(number => NumberText(number), number => number);
        options.Add(FirstText, -1);
        var initial = await source.InsertAsync(new LatticeRecord(source.EntityType)
        {
            ["Id"] = Key,
            ["Label"] = FirstText,
            ["Single"] = (float[])[Float(FirstFloatNaN), Float(LaterFloatNaN), -0.0f, +0.0f, .. fillers.Select(number => (float)number)],
            ["Double"] = (double[])[Double(FirstDoubleNaN), Double(LaterDoubleNaN), -0.0d, +0.0d, .. fillers.Select(number => (double)number)],
            ["Text"] = (string[])[FirstText, EquivalentText, .. fillers.Select(NumberText)],
            ["Options"] = options,
        });

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 23 });
        Assert.Equal(initial.Position, child.Position);
        var replicas = child.Set("Item");
        var revision = Expect(source, fillers, -1);
        Assert.Equal(revision, Expect(replicas, fillers, -1));

        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var masterRead = OwnThread.Run(() => ExpectHeld(source, masterPause));
        var childRead = OwnThread.Run(() => ExpectHeld(replicas, childPause));
        try
        {
            await masterPause.EnteredAsync(masterRead);
            await childPause.EnteredAsync(childRead);
            var update = replicas.CreateUpdate(Key);
            var singleNaN = update.SetAdd("Single", Float(LaterFloatNaN));
            var singleZero = update.SetAdd("Single", +0.0f);
            var doubleNaN = update.SetAdd("Double", Double(LaterDoubleNaN));
            var doubleZero = update.SetAdd("Double", +0.0d);
            var text = update.SetAdd("Text", EquivalentText);
            var map = update.MapSet("Options", EquivalentText, -2);
            var result = await update.CommitAsync(revision).AsTask().WaitAsync(Bound);
            Assert.Equal(new LatticePosition(initial.Position.History, initial.Position.Sequence + 1), result.Position);
            Assert.Equal(result.Position, master.Position);
            Assert.Equal(result.Position, child.Position);
            foreach (var unchanged in new[] { singleNaN, singleZero, doubleNaN, doubleZero, text })
            {
                Assert.False(unchanged.Changed);
            }

            Assert.True(map.Changed);
            var nextRevision = Expect(source, fillers, -2);
            Assert.NotEqual(revision, nextRevision);
            Assert.Equal(nextRevision, Expect(replicas, fillers, -2));
            Assert.True(master.Counters.Arena.RetiredBytes > 0);
            Assert.True(child.Counters.Arena.RetiredBytes > 0);
        }
        finally
        {
            masterPause.Release();
            childPause.Release();
            await Task.WhenAll(masterRead, childRead).WaitAsync(Bound);
        }

        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);

        void ExpectHeld(ILatticeSet set, ViewerPause pause)
        {
            Assert.True(set.TryView(Key, view =>
            {
                var optionsView = view["Options"].Map;
                var textView = view["Text"].Set;
                Assert.True(optionsView.TryGetValue(EquivalentText, out var held));
                Assert.Equal(-1, held.Get<int>());
                var cursor = textView.GetEnumerator();
                Assert.True(cursor.MoveNext());
                var first = cursor.Current.GetString();
                var copy = cursor;

                pause.Hold();

                Assert.Equal(-1, held.Get<int>());
                Assert.Equal(first, copy.Current.GetString());
                ExpectRepresentations(view, fillers, -1);
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        }
    }

    private static LatticeRevision Expect(ILatticeSet set, int[] fillers, int mapValue)
    {
        Assert.True(set.TryGet(Key, out var copy, out var revision));
        var singles = copy.Get<HashSet<float>>("Single")!;
        var doubles = copy.Get<HashSet<double>>("Double")!;
        var texts = copy.Get<HashSet<string>>("Text")!;
        var options = copy.Get<Dictionary<string, int>>("Options")!;
        ExpectRepresentations(singles, doubles, texts, fillers);
        Assert.Equal(mapValue, options[EquivalentText]);
        Assert.Contains(FirstText, options.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(EquivalentText, options.Keys, StringComparer.Ordinal);
        Assert.Equal(Key, Assert.Single(set.Index("ByLabel").Find(IndexKey.Equal(EquivalentText))).Get<long>("Id"));

        Assert.True(set.TryView(Key, view =>
        {
            ExpectRepresentations(view, fillers, mapValue);
            return view.Revision;
        }, out var seen));
        Assert.Equal(revision, seen);

        return revision;
    }

    private static void ExpectRepresentations(LatticeRecordView view, int[] fillers, int mapValue)
    {
        var singles = view["Single"].Set;
        var doubles = view["Double"].Set;
        var texts = view["Text"].Set;
        var options = view["Options"].Map;

        Assert.True(singles.Contains(Float(LaterFloatNaN)));
        Assert.True(singles.Contains(+0.0f));
        Assert.True(doubles.Contains(Double(LaterDoubleNaN)));
        Assert.True(doubles.Contains(+0.0d));
        Assert.True(texts.Contains(EquivalentText));
        Assert.True(options.TryGetValue(EquivalentText, out var found));
        Assert.Equal(mapValue, found.Get<int>());
        Assert.False(options.TryGetValue("absent", out _));

        var floatMembers = new List<float>();
        foreach (var member in singles)
        {
            floatMembers.Add(member.Get<float>());
        }

        var doubleMembers = new List<double>();
        foreach (var member in doubles)
        {
            doubleMembers.Add(member.Get<double>());
        }

        var textMembers = new List<string>();
        foreach (var member in texts)
        {
            textMembers.Add(member.GetString());
        }

        var afterMap = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in options)
        {
            afterMap.Add(entry.Key.GetString(), entry.Value.Get<int>());
        }

        Assert.Equal(mapValue, afterMap[FirstText]);
        Assert.False(afterMap.ContainsKey(EquivalentText));
        ExpectRepresentations(floatMembers, doubleMembers, textMembers, fillers);
    }

    private static void ExpectRepresentations(IEnumerable<float> singles, IEnumerable<double> doubles, IEnumerable<string> texts, int[] fillers)
    {
        uint[] singleBits = [FirstFloatNaN, 0x8000_0000, .. fillers.Select(number => BitConverter.SingleToUInt32Bits(number))];
        ulong[] doubleBits = [FirstDoubleNaN, 0x8000_0000_0000_0000, .. fillers.Select(number => BitConverter.DoubleToUInt64Bits(number))];
        string[] strings = [FirstText, .. fillers.Select(NumberText)];
        Assert.Equal(singleBits.Order(), singles.Select(BitConverter.SingleToUInt32Bits).Order());
        Assert.Equal(doubleBits.Order(), doubles.Select(BitConverter.DoubleToUInt64Bits).Order());
        Assert.Equal(strings.Order(StringComparer.Ordinal), texts.Order(StringComparer.Ordinal));
    }

    private static string NumberText(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static float Float(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    private static double Double(ulong bits) => BitConverter.UInt64BitsToDouble(bits);
}
