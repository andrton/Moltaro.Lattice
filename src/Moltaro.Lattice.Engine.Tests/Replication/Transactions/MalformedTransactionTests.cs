using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// A transaction a child receives is checked by the check of its frames, its announced length and its
// sequence, which follows the one before, and what its effects carry is checked before it is used
// (brief of Epic 1, sprint S11, Acceptance: the encoding "refuses malformed input"; decided after the
// study, point 6). A malformed transaction is refused whole: nothing of it is applied, also not a valid
// effect before the one that is not, and nothing after it either, since the child cannot apply a later
// transaction without it. The child ends its connection, as for a frame that is not valid (S10), and
// keeps serving its reads at the position it had, Ready: it stops following, as it does when its arena
// cannot hold a change; it is not Faulted, which is for a stream that diverges (DivergenceTests).
//
// The master is real and holds one root the test can find in a frame (MarkedRoot); the child loads it
// and is Ready. A relay (FrameRelay) changes the first transaction the master sends after that — in the
// shapes TransactionPayload states —, with a valid check unless the check is the damage, and passes
// every other frame; the master then writes once more, so that a frame follows the damaged one. The
// child holds what it loaded and nothing else: no root entry of a refused effect is left either.
public sealed class MalformedTransactionTests
{
    public static IEnumerable<TheoryDataRow<string>> Damages =>
    [
        new("a sequence that skips one"),
        new("an announced length shorter than its effects"),
        new("an announced length longer than its effects"),
        new("an unknown kind of effect"),
        new("an unknown type id in a Write"),
        new("an unknown type id in a Delete"),
        new("a Write whose root has a descriptor that does not match its length"),
        new("a Write whose root announces fewer bytes than it holds"),
        new("a frame whose check is wrong"),
        new("a valid Write followed by an invalid one in the same transaction"),
    ];

    [Theory(Skip = ChildFollowing.NotImplemented)]
    [MemberData(nameof(Damages))]
    public async Task Malformed_transaction_is_refused_whole_and_nothing_of_it_or_after_it_is_applied(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedRoot.Schema);
        var records = master.Set("Marked");
        await records.InsertAsync(MarkedRoot.Record());
        WireFrame? held = null;
        var transactions = 0;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction)
            {
                return [frame];
            }

            transactions++;

            if (damage == "a valid Write followed by an invalid one in the same transaction")
            {
                switch (transactions)
                {
                    case 1:
                        held = frame;
                        return [];

                    case 2:
                        return [Merged(held!, frame)];
                }
            }

            return transactions == 1 ? [Damaged(frame, damage)] : [frame];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var readiness = child.Position;

        if (damage == "an unknown type id in a Delete")
        {
            await records.DeleteAsync("code");
        }
        else
        {
            await records.InsertAsync(MarkedRoot.Record("second"));
        }

        if (damage == "a valid Write followed by an invalid one in the same transaction")
        {
            await records.InsertAsync(MarkedRoot.Record("third"));
        }

        await records.InsertAsync(MarkedRoot.Record("after"));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);

        var onChild = child.Set("Marked");

        Assert.Equal("name", onChild.Get("code")?["Name"]);
        Assert.Null(onChild.Get("second"));
        Assert.Null(onChild.Get("third"));
        Assert.Null(onChild.Get("after"));
        Assert.Single(onChild.Enumerate());

        child.DrainRetired();
        Assert.Equal(1, child.Counters.RootEntries);
    }

    // What the relay sends in the place of the first transaction, a Write of the record "second" or, for
    // the damage of a Delete, the delete of "code".
    private static WireFrame Damaged(WireFrame frame, string damage)
    {
        var sequence = TransactionPayload.SequenceOf(frame);
        var effects = TransactionPayload.EffectsOf(frame);

        Assert.Equal((ulong)effects.Length, TransactionPayload.AnnouncedLengthOf(frame));

        switch (damage)
        {
            case "a sequence that skips one":
                return frame.WithPayload(TransactionPayload.Make(sequence + 1, effects));

            case "an announced length shorter than its effects":
                return frame.WithPayload(TransactionPayload.Make(sequence, (ulong)effects.Length - 1, effects));

            case "an announced length longer than its effects":
                return frame.WithPayload(TransactionPayload.Make(sequence, (ulong)effects.Length + 1, effects));

            case "an unknown kind of effect":
                effects[0] = 99;
                break;

            case "an unknown type id in a Write":
                Assert.Equal(TransactionPayload.Write, effects[0]);
                BinaryPrimitives.WriteUInt32LittleEndian(effects.AsSpan(MarkedRoot.TypeIdAt(effects)), MarkedRoot.TypeId + 1);
                break;

            case "an unknown type id in a Delete":
                Assert.Equal(TransactionPayload.Delete, effects[0]);
                Assert.Equal(MarkedRoot.TypeId, BinaryPrimitives.ReadUInt32LittleEndian(effects.AsSpan(1)));
                BinaryPrimitives.WriteUInt32LittleEndian(effects.AsSpan(1), MarkedRoot.TypeId + 1);
                break;

            case "a Write whose root has a descriptor that does not match its length":
                DamageDescriptor(effects);
                break;

            case "a Write whose root announces fewer bytes than it holds":
                Assert.Equal(TransactionPayload.Write, effects[0]);

                var rootLength = BinaryPrimitives.ReadUInt64LittleEndian(effects.AsSpan(1));

                Assert.Equal((ulong)effects.Length - 1 - sizeof(ulong), rootLength);
                BinaryPrimitives.WriteUInt64LittleEndian(effects.AsSpan(1), rootLength - 1);
                break;

            case "a frame whose check is wrong":
                return frame.WithDamagedCheck();

            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }

        return frame.WithPayload(TransactionPayload.Make(sequence, effects));
    }

    // One transaction in the place of two, at the place and the sequence of the first: the Write of the
    // first, valid, and the Write of the second with a damaged root, its last mutation the sequence of
    // the one transaction, as a master writes the members of a batch.
    private static WireFrame Merged(WireFrame first, WireFrame second)
    {
        var sequence = TransactionPayload.SequenceOf(first);
        var damaged = TransactionPayload.EffectsOf(second);

        BinaryPrimitives.WriteUInt64LittleEndian(damaged.AsSpan(TransactionPayload.WriteLastMutationAt), sequence);
        DamageDescriptor(damaged);

        return first.WithPayload(TransactionPayload.Make(sequence, [.. TransactionPayload.EffectsOf(first), .. damaged]));
    }

    // Name is inline and 4 bytes long; a chunked value is longer than a block of its own holds.
    private static void DamageDescriptor(byte[] effects)
    {
        Assert.Equal(TransactionPayload.Write, effects[0]);

        var descriptorAt = MarkedRoot.PayloadAt(effects) + (MarkedRoot.FieldOf("Name").Offset - BlockHeader.Size);

        BinaryPrimitives.WriteUInt32LittleEndian(effects.AsSpan(descriptorAt), (uint)DescriptorKindEnum.Chunked);
    }
}
