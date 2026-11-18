using System.Buffers.Binary;
using System.Text;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// A transaction a child receives is checked by the check of its frames, its announced length and its
// sequence, which follows the one before, and what its effects carry is checked before it is used
// (brief of Epic 1, sprint S11, Acceptance: the encoding "refuses malformed input"; decided after the
// study, point 6). The root of a Write carries the sequence of its transaction as its last mutation, and
// one that carries an earlier or a later sequence is malformed (the coordinator's decision of 17 November
// 2026). A transaction names each key once, the key of a Write is well-formed text as the key of a Delete
// is, and the key of a Delete is a key of its entity type (review round 1 of S11). A malformed transaction
// is refused whole: nothing of it is applied, also not a valid effect before the one that is not, and
// nothing after it either, since the child cannot apply a later transaction without it. The child ends
// its connection, as for a frame that is not valid (S10), and keeps serving its reads at the position it
// had, Ready: it stops following, as it does when its arena cannot hold a change.
//
// The master is real and holds one root the test can find in a frame (MarkedRoot); the child loads it
// and is Ready. A relay (FrameRelay) changes the first transaction the master sends after that — in the
// shapes TransactionPayload states —, with a valid check unless the check is the damage, and passes
// every other frame; the master then writes once more, so that a frame follows the damaged one. Two
// rows merge the first two transactions into one; one damages the last frame of a transaction of
// several. The child holds what it loaded and nothing else: no root entry of a refused effect is left
// either. A key of an integer type outside its type is stated on a schema of such a key.
public sealed class MalformedTransactionTests
{
    private const string DeleteOfAnUnknownType = "an unknown type id in a Delete";

    private const string DeleteOfTooShortAKey = "a Delete whose key announces more text than it holds";

    private const string ValidThenInvalid = "a valid Write followed by an invalid one in the same transaction";

    private const string KeyTwice = "one key written twice in the same transaction";

    private const string LastFrameTooLong = "a last frame longer than what remains of its transaction";

    private const string KeyNotUtf8 = "a Write whose key is not well-formed UTF-8";

    private const string KeyWithALoneSurrogate = "a Write whose key holds a lone surrogate in UTF-16";

    public static IEnumerable<TheoryDataRow<string>> Damages =>
    [
        new("a sequence that skips one"),
        new("an announced length shorter than its effects"),
        new("an announced length longer than its effects"),
        new("an unknown kind of effect"),
        new("an unknown type id in a Write"),
        new(DeleteOfAnUnknownType),
        new(DeleteOfTooShortAKey),
        new("a Write whose root has a descriptor that does not match its length"),
        new("a Write whose root announces fewer bytes than it holds"),
        new("a Write whose root carries an earlier sequence than its transaction"),
        new("a Write whose root carries a later sequence than its transaction"),
        new(KeyNotUtf8),
        new(KeyWithALoneSurrogate),
        new("a frame whose check is wrong"),
        new(LastFrameTooLong),
        new(ValidThenInvalid),
        new(KeyTwice),
    ];

    [Theory]
    [MemberData(nameof(Damages))]
    public async Task Malformed_transaction_is_refused_whole_and_nothing_of_it_or_after_it_is_applied(string damage)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(damage == KeyWithALoneSurrogate ? MarkedRoot.Utf16Schema : MarkedRoot.Schema);
        var records = master.Set("Marked");
        var entityType = records.EntityType;
        await records.InsertAsync(MarkedRoot.RecordOf(entityType, "code"));
        WireFrame? held = null;
        var transactions = 0;
        var lastFrameDamaged = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction)
            {
                return [frame];
            }

            transactions++;

            if (damage is ValidThenInvalid or KeyTwice)
            {
                switch (transactions)
                {
                    case 1:
                        held = frame;
                        return [];

                    case 2:
                        return [Merged(held!, frame, damageTheSecond: damage == ValidThenInvalid)];
                }
            }

            if (damage == LastFrameTooLong)
            {
                // The first frame that ends a transaction ends the transaction of several frames.
                if (!frame.EndsABody || lastFrameDamaged)
                {
                    return [frame];
                }

                lastFrameDamaged = true;
                return [frame.WithPayload([.. frame.Payload, 0])];
            }

            return transactions == 1 ? [Damaged(frame, damage)] : [frame];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var readiness = child.Position;

        switch (damage)
        {
            case DeleteOfAnUnknownType or DeleteOfTooShortAKey:
                await records.DeleteAsync("code");
                break;

            case ValidThenInvalid:
                await records.InsertAsync(MarkedRoot.RecordOf(entityType, "second"));
                await records.InsertAsync(MarkedRoot.RecordOf(entityType, "third"));
                break;

            case KeyTwice:
                await records.InsertAsync(MarkedRoot.RecordOf(entityType, "second"));
                await records.ReplaceAsync(MarkedRoot.RecordOf(entityType, "second"));
                break;

            case LastFrameTooLong:
                var large = MarkedRoot.RecordOf(entityType, "second");

                large["Data"] = new byte[200_000];
                await records.InsertAsync(large);
                break;

            default:
                await records.InsertAsync(MarkedRoot.RecordOf(entityType, "second"));
                break;
        }

        await records.InsertAsync(MarkedRoot.RecordOf(entityType, "after"));
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

    [Fact]
    public async Task Delete_whose_integer_key_is_outside_its_key_type_is_refused_and_nothing_of_it_or_after_it_is_applied()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(KeySchema.Of("Utf8", "Ordinal", "UInt8"));
        var records = master.Set("UInt8");
        await records.InsertAsync(KeySchema.RecordOf(records, (byte)7, "seven"));
        var transactions = 0;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || ++transactions > 1)
            {
                return [frame];
            }

            // The key of the Delete, an integer key as an Int64 after the kind and the type id, becomes
            // one no UInt8 holds.
            var effects = TransactionPayload.EffectsOf(frame);
            var keyAt = 1 + sizeof(uint);

            Assert.Equal(TransactionPayload.Delete, effects[0]);
            Assert.Equal(7L, BinaryPrimitives.ReadInt64LittleEndian(effects.AsSpan(keyAt)));
            BinaryPrimitives.WriteInt64LittleEndian(effects.AsSpan(keyAt), 300);

            return [frame.WithPayload(TransactionPayload.Make(TransactionPayload.SequenceOf(frame), effects))];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var readiness = child.Position;

        await records.DeleteAsync((byte)7);
        await records.InsertAsync(KeySchema.RecordOf(records, (byte)8, "eight"));
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.Equal("seven", child.Set("UInt8").Get((byte)7)?["Body"]);
        Assert.Null(child.Set("UInt8").Get((byte)8));
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

            case DeleteOfAnUnknownType:
                Assert.Equal(TransactionPayload.Delete, effects[0]);
                Assert.Equal(MarkedRoot.TypeId, BinaryPrimitives.ReadUInt32LittleEndian(effects.AsSpan(1)));
                BinaryPrimitives.WriteUInt32LittleEndian(effects.AsSpan(1), MarkedRoot.TypeId + 1);
                break;

            case DeleteOfTooShortAKey:
                // A String key travels as the length of its UTF-8 and that UTF-8, after the kind and the
                // type id; it announces one byte more than the effect holds.
                var keyAt = 1 + sizeof(uint);
                var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(effects.AsSpan(keyAt));

                Assert.Equal(TransactionPayload.Delete, effects[0]);
                Assert.Equal((uint)(effects.Length - keyAt - sizeof(uint)), keyLength);
                BinaryPrimitives.WriteUInt32LittleEndian(effects.AsSpan(keyAt), keyLength + 1);
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

            case "a Write whose root carries an earlier sequence than its transaction":
                SetLastMutation(effects, sequence, sequence - 1);
                break;

            case "a Write whose root carries a later sequence than its transaction":
                SetLastMutation(effects, sequence, sequence + 1);
                break;

            case KeyNotUtf8:
                // Bytes that no UTF-8 holds, as many as the key "second" takes.
                effects.AsSpan(MarkedRoot.KeyAt(effects, "second", Encoding.UTF8), "second".Length).Fill(0xFF);
                break;

            case KeyWithALoneSurrogate:
                // A high surrogate in the place of the first code unit, with no low one after it.
                BinaryPrimitives.WriteUInt16LittleEndian(effects.AsSpan(MarkedRoot.KeyAt(effects, "second", Encoding.Unicode)), 0xD800);
                break;

            case "a frame whose check is wrong":
                return frame.WithDamagedCheck();

            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }

        return frame.WithPayload(TransactionPayload.Make(sequence, effects));
    }

    // One transaction in the place of two, at the place and the sequence of the first: the Write of the
    // first and the Write of the second, its last mutation the sequence of the one transaction, as a
    // master writes the members of a batch, its root damaged when the row says so.
    private static WireFrame Merged(WireFrame first, WireFrame second, bool damageTheSecond)
    {
        var sequence = TransactionPayload.SequenceOf(first);
        var member = TransactionPayload.EffectsOf(second);

        BinaryPrimitives.WriteUInt64LittleEndian(member.AsSpan(TransactionPayload.WriteLastMutationAt), sequence);

        if (damageTheSecond)
        {
            DamageDescriptor(member);
        }

        return first.WithPayload(TransactionPayload.Make(sequence, [.. TransactionPayload.EffectsOf(first), .. member]));
    }

    // The last mutation of the root of the Write, which the master wrote as the sequence of the
    // transaction, becomes the one given.
    private static void SetLastMutation(byte[] effects, ulong sequence, ulong lastMutation)
    {
        Assert.Equal(TransactionPayload.Write, effects[0]);
        Assert.Equal(sequence, BinaryPrimitives.ReadUInt64LittleEndian(effects.AsSpan(TransactionPayload.WriteLastMutationAt)));

        BinaryPrimitives.WriteUInt64LittleEndian(effects.AsSpan(TransactionPayload.WriteLastMutationAt), lastMutation);
    }

    // Name is inline and 4 bytes long; a chunked value is longer than a block of its own holds.
    private static void DamageDescriptor(byte[] effects)
    {
        Assert.Equal(TransactionPayload.Write, effects[0]);

        var descriptorAt = MarkedRoot.PayloadAt(effects) + (MarkedRoot.FieldOf("Name").Offset - BlockHeader.Size);

        BinaryPrimitives.WriteUInt32LittleEndian(effects.AsSpan(descriptorAt), (uint)DescriptorKindEnum.Chunked);
    }
}
