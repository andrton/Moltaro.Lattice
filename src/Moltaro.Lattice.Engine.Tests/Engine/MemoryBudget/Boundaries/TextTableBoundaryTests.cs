using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Budgeting;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Boundaries;

// The text table grows its arrays as distinct Dictionary texts arrive. The first insert of a new
// text that makes the table add an array, and nothing else, is admitted exactly when the limit
// holds what the instance holds, the estimate of the write and that array, sized by its element type
// and its length. The primary index is presized and Depot has no secondary index, so the text table
// is the one structure that grows.
//
// The inserts before it grow other arrays of the text table by a copy, which holds the old and the new
// array for a moment. They are replayed with the room of a held array beside the boundary, which the
// test then holds, so that the room left is exactly the boundary and the limit gives its byte.
public sealed class TextTableBoundaryTests
{
    private const int MostInserts = 4_096;

    // The length of the array held beside the boundary: a segment, far more than the peaks of the
    // replayed inserts take.
    private const int HeldLength = Segment;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task The_insert_whose_new_text_grows_a_text_table_array_is_admitted_exactly_when_the_array_fits(int difference)
    {
        var growing = 0;
        long before;
        HeldArray added;
        var witness = new CountingNativeAllocator();
        await using (var control = await OpenAsync(32L * Segment, witness, schema: TextSchema.Utf8))
        {
            await PresizeAsync(control);
            var depots = control.Set<TextSchema.Depot>();
            HeldArray? found = null;
            before = 0;
            for (var key = 1; key <= MostInserts && found is null; key++)
            {
                var earlier = ExpectAtRest(control, witness);
                await depots.InsertAsync(Depot(key));
                var later = ExpectAtRest(control, witness);
                var more = Added(later.Arrays, earlier.Arrays);
                if (key > 2 && more.Count == 1 && Added(earlier.Arrays, later.Arrays).Count == 0)
                {
                    Assert.Equal("TextTable", more[0].Owner);
                    growing = key;
                    before = earlier.Charged;
                    found = more[0];
                    Assert.Equal(before + ArrayBytes(more[0]), later.Charged);
                }
            }

            added = Assert.IsType<HeldArray>(found);
        }

        var required = before + BatchInputCharge.PerMemberBytes + ArrayBytes(added);
        var held = ArrayBytes(typeof(byte), HeldLength);
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(required + held + difference, allocator, schema: TextSchema.Utf8);
        await PresizeAsync(node);
        var set = node.Set<TextSchema.Depot>();
        for (var key = 1; key < growing; key++)
        {
            await set.InsertAsync(Depot(key));
        }

        Assert.Equal(before, ExpectAtRest(node, allocator).Charged);
        using var hold = node.HoldBudgetBacking(HeldLength);
        var texts = node.Counters.TextEntries;
        var insert = set.InsertAsync(Depot(growing)).AsTask();
        if (difference < 0)
        {
            await RefusedAsync(insert, LatticeLimitEnum.Memory);
            Assert.Null(set.Get((long)growing));
            Assert.Equal(texts, node.Counters.TextEntries);
            Assert.Equal(before + held, ExpectAtRest(node, allocator).Charged);
        }
        else
        {
            await insert;
            Assert.Equal(Text(growing), set.Get((long)growing)?.Area);
            Assert.Equal(before + held + ArrayBytes(added), ExpectAtRest(node, allocator).Charged);
        }
    }

    private static Task<bool> PresizeAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Tables.Stored(2).Presize(MostInserts + 1);
            return true;
        });

    private static TextSchema.Depot Depot(int key) => new() { Id = key, Area = Text(key) };

    private static string Text(int key) => $"area-{key:D5}";
}
