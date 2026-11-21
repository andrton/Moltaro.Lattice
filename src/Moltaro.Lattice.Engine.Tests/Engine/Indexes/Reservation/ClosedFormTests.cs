using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// The need of the writes that wait on an index has a closed form, OrderedTree.Need, which costs the same
// for any number of writes and is the sum the tree first took one write at a time (ReservationNeed;
// docs/design/ordered-index.md, "The reservation"). The two agree over heights 0 to 4; a root with no
// room, with room for one insert, for L − 1 and for L; an L of 2 and the L of an index of one Int64 part;
// 0 to 3 L + 2 inserts and 0 to 3 removes. The member is held against the sum, not the pool of a tree,
// because the standing reserve keeps a pool above the need of the writes that wait.
public sealed class ClosedFormTests
{
    [Fact]
    public void Closed_form_of_the_need_of_waiting_writes_equals_the_sum_over_the_writes_at_every_height_room_and_count()
    {
        var differ = new List<string>();

        foreach (var least in new[] { 2, LeastSeparatorsOfAnInt64Index() })
        {
            foreach (var room in new[] { 0, 1, least - 1, least }.Distinct())
            {
                for (var height = 0; height <= 4; height++)
                {
                    for (var inserts = 0; inserts <= (3 * least) + 2; inserts++)
                    {
                        for (var removes = 0; removes <= 3; removes++)
                        {
                            var sum = ReservationNeed.Of(height, room, least, inserts, removes);
                            var closed = OrderedTree.Need(height, room, least, inserts, removes);

                            if (closed != sum)
                            {
                                differ.Add($"height {height}, room {room}, L {least}, {inserts} inserts, {removes} removes: {closed} against {sum}");
                            }
                        }
                    }
                }
            }
        }

        Assert.Empty(differ);
    }

    // The separators an inner node of the index ByNumber of Counted, one Int64 part, holds before it is
    // full.
    private static int LeastSeparatorsOfAnInt64Index()
    {
        var schema = IndexSchema.Utf8;

        Assert.True(schema.TryGetEntityType("Counted", out var counted));
        Assert.True(counted.TryGetIndex("ByNumber", out var byNumber));

        return IndexLayout.Compile(byNumber, RootLayout.Compile(counted, schema)).LeastSeparators;
    }
}
