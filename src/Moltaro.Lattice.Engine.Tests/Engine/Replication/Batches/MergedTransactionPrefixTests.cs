using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Batches;

// A child exposes a transaction of several effects whole while it applies it (the brief of Epic 1, sprint
// S17, Acceptance: "A child never acknowledges a part. A transaction with several effects is exposed whole or
// not at all ... on a child that applies it"; the finding of the breaker of S15, part 1, whose probe this is).
// A relay (FrameRelay) sends, in the place of the transactions of 300 inserts of the master, one transaction
// at the sequence s of the first, whose Writes all carry s as their last mutation, as a master writes the
// members of a batch. Member 1 is A and member 300 is B, both of the tag "k"; the members between are
// fillers. While the child applies it, readers on the child look, again and again:
// - by key: A, then B at once, and B, then A: a read that finds one finds the other;
// - a search of the tag "k": an answer holds both or neither;
// - an enumeration with a session: a member is returned only once the session is at s or later.
// The probe of the breaker found an answer that held A and not B in 10 of 10 runs of 300 members.
public sealed class MergedTransactionPrefixTests
{
    private const int Members = 300;

    public static IEnumerable<TheoryDataRow<int>> Runs => Enumerable.Range(0, 3).Select(run => new TheoryDataRow<int>(run));

    [Theory]
    [MemberData(nameof(Runs))]
    public async Task Child_that_applies_a_transaction_of_many_effects_shows_none_of_it_or_all_of_it(int run)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema);
        var held = new List<WireFrame>();
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction)
            {
                return [frame];
            }

            Assert.True(frame.EndsABody, "A member transaction spans more than one frame.");
            held.Add(frame);

            return held.Count < Members ? [] : [Merged(held)];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var s = PositionSequence.Of(child.Position) + 1;
        var items = child.Set("Item");
        using var done = new CancellationTokenSource();
        var seen = new ConcurrentQueue<string>();

        var byKey = OwnThread.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var a = items.Get(1L);
                var b = items.Get((long)Members);

                if (a is not null && b is null)
                {
                    seen.Enqueue("by key: A, then not B");
                }

                b = items.Get((long)Members);
                a = items.Get(1L);

                if (b is not null && a is null)
                {
                    seen.Enqueue("by key: B, then not A");
                }

                if (a is not null && b is not null)
                {
                    return;
                }
            }
        });

        var search = OwnThread.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var answer = OwnThread.Wait(items.Index("ByTag").FindAsync(IndexKey.Equal("k"), new LatticeFindOptions()));
                var ids = answer.Items.Select(item => item.Get<long>("Id")).ToList();

                if (ids.Contains(1L) != ids.Contains(Members))
                {
                    seen.Enqueue($"search: [{string.Join(", ", ids)}] at {PositionSequence.Of(answer.Position)}");
                }

                if (ids.Count == 2)
                {
                    return;
                }
            }
        });

        var enumerate = OwnThread.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var session = new LatticeSession();
                var enumerator = items.EnumerateAsync(session).GetAsyncEnumerator();
                var members = 0;

                try
                {
                    while (OwnThread.Wait(enumerator.MoveNextAsync()))
                    {
                        members++;

                        if (PositionSequence.Of(session.MinPosition!.Value) < s)
                        {
                            seen.Enqueue($"enumeration: member {enumerator.Current.Get<long>("Id")} at {PositionSequence.Of(session.MinPosition!.Value)}");
                        }
                    }
                }
                finally
                {
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                if (members == Members)
                {
                    return;
                }
            }
        });

        var set = master.Set("Item");

        for (var id = 1L; id <= Members; id++)
        {
            var tag = id == 1 || id == Members ? "k" : "f" + id.ToString(CultureInfo.InvariantCulture);

            await set.InsertAsync(new LatticeRecord(set.EntityType) { ["Id"] = id, ["Tag"] = tag, ["Group"] = id % 7 });
        }

        await Eventually.HoldsAsync(() => PositionSequence.Of(child.Position) == s, "The child did not apply the merged transaction.");
        await Task.WhenAll(byKey, search, enumerate).WaitAsync(Bound);
        await done.CancelAsync();

        Assert.True(seen.IsEmpty, $"run {run}: {string.Join(" | ", seen)}");
    }

    // One transaction at the sequence of the first held one, with the effects of all of them, each Write
    // carrying that sequence as its last mutation (TransactionPayload).
    private static WireFrame Merged(List<WireFrame> held)
    {
        var sequence = TransactionPayload.SequenceOf(held[0]);
        var effects = new List<byte>();

        foreach (var frame in held)
        {
            var member = TransactionPayload.EffectsOf(frame);

            Assert.Equal(TransactionPayload.Write, member[0]);
            BinaryPrimitives.WriteUInt64LittleEndian(member.AsSpan(TransactionPayload.WriteLastMutationAt), sequence);
            effects.AddRange(member);
        }

        return held[0].WithPayload(TransactionPayload.Make(sequence, [.. effects]));
    }

    private static LatticeSchema Schema { get; } = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Tag", "Type": "String" },
                { "Id": 3, "Name": "Group", "Type": "Int64" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByTag", "Fields": [2] }, { "Id": 2, "Name": "ByGroup", "Fields": [3] } ]
            }
          ]
        }
        """);
}
