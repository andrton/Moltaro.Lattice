using System.Buffers.Binary;
using System.Text;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// A transaction names each key once (the rule of a child that applies what it receives,
// Engine.Child.Loading.ReceivedChanges; MalformedTransactionTests states it for two effects that are both
// applied), and so does one whose effect on a key the rule skips: a delete of a key the child does not
// hold, and a state the child holds already, count as a naming of the key as much as an applied effect.
// A transaction that names a key again after such an effect is malformed and refused whole.
// - After Ready, in the place of the transaction that writes "second", the relay (FrameRelay) passes one
//   that first deletes "second", which the child does not hold, and then writes it. The child ends its
//   connection and keeps serving its reads at the position it had, Ready, without "second" or anything
//   after it.
// - Before Ready, the relay announces in LoadEnd a start one sequence before the readiness position and
//   passes after LoadEnd a transaction at that position, which first writes the state of "code" the load
//   copied — the child holds it at that sequence already, so the rule skips it — and then writes or
//   deletes "code". The open of the child fails with Unavailable.
// - A transaction of many effects whose last effect names the key of its first, as a forged batch would: the
//   relay passes, in the place of the transactions of 199 inserts and a replace of the first of them, one
//   at the sequence of the first, as a master writes the members of a batch. The child refuses it whole, as
//   above: a transaction names each key once however many effects lie between, which the child checks with
//   one set of the keys of a transaction (Entities.PublicationKeys).
// The shapes of the frames are those TransactionPayload states; the master holds one root of MarkedRoot.
public sealed class KeyNamedOnceTests
{
    public static IEnumerable<TheoryDataRow<string>> AfterASkippedWrite =>
    [
        new("a Write of it"),
        new("a Delete of it"),
    ];

    [Fact]
    public async Task Transaction_that_deletes_a_key_the_child_does_not_hold_and_then_writes_it_is_refused_whole()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedRoot.Schema);
        var records = master.Set("Marked");
        await records.InsertAsync(MarkedRoot.RecordOf(records.EntityType, "code"));
        var transactions = 0;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || ++transactions > 1)
            {
                return [frame];
            }

            var effects = TransactionPayload.EffectsOf(frame);

            Assert.Equal(TransactionPayload.Write, effects[0]);

            return [frame.WithPayload(TransactionPayload.Make(TransactionPayload.SequenceOf(frame), [.. DeleteOf("second"), .. effects]))];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var readiness = child.Position;
        var childEnded = relay.ChildEndedAsync();

        await records.InsertAsync(MarkedRoot.RecordOf(records.EntityType, "second"));
        var after = await records.InsertAsync(MarkedRoot.RecordOf(records.EntityType, "after"));

        await Eventually.HoldsAsync(
            () => childEnded.IsCompleted || child.Position.Equals(after.Position),
            "Within the bound the child neither ended its connection nor applied what its master sent.");

        Assert.True(childEnded.IsCompleted, "The child applied a transaction that names a key twice.");
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.Null(child.Set("Marked").Get("second"));
        Assert.Null(child.Set("Marked").Get("after"));
        Assert.Equal("name", child.Set("Marked").Get("code")?["Name"]);
    }

    [Fact]
    public async Task Transaction_of_many_effects_whose_last_names_the_key_of_its_first_is_refused_whole()
    {
        const int Inserts = 199;

        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedRoot.Schema);
        var records = master.Set("Marked");
        await records.InsertAsync(MarkedRoot.RecordOf(records.EntityType, "code"));
        var held = new List<WireFrame>();
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || held.Count > Inserts)
            {
                return [frame];
            }

            held.Add(frame);

            if (held.Count <= Inserts)
            {
                return [];
            }

            var sequence = TransactionPayload.SequenceOf(held[0]);
            var effects = new List<byte>();

            foreach (var member in held)
            {
                var effect = TransactionPayload.EffectsOf(member);

                Assert.Equal(TransactionPayload.Write, effect[0]);
                BinaryPrimitives.WriteUInt64LittleEndian(effect.AsSpan(TransactionPayload.WriteLastMutationAt), sequence);
                effects.AddRange(effect);
            }

            return [held[0].WithPayload(TransactionPayload.Make(sequence, [.. effects]))];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var readiness = child.Position;

        for (var index = 0; index < Inserts; index++)
        {
            await records.InsertAsync(MarkedRoot.RecordOf(records.EntityType, $"member {index}"));
        }

        await records.ReplaceAsync(MarkedRoot.RecordOf(records.EntityType, "member 0"));
        await relay.ChildEndedAsync().WaitAsync(Bound);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.Null(child.Set("Marked").Get("member 0"));
        Assert.Null(child.Set("Marked").Get($"member {Inserts - 1}"));
        Assert.Single(child.Set("Marked").Enumerate());
    }

    [Theory]
    [MemberData(nameof(AfterASkippedWrite))]
    public async Task Transaction_that_names_a_key_again_after_a_write_of_it_the_child_skips_fails_the_open_of_the_child(string then)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(MarkedRoot.Schema);
        var records = master.Set("Marked");
        await records.InsertAsync(MarkedRoot.RecordOf(records.EntityType, "code"));
        byte[]? root = null;
        var added = 0UL;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            var passed = frame.WithStreamSequence(frame.StreamSequence + added);

            switch (frame.Kind)
            {
                case FrameKindEnum.Blocks:
                    root = BlocksPayload.OnlyRootIn(frame);

                    return [passed];

                case FrameKindEnum.LoadEnd:
                    var end = LoadEndPayload.Of(frame);
                    var written = WriteOf(root!);
                    var effects = then == "a Write of it" ? [.. written, .. written] : written.Concat(DeleteOf("code")).ToArray();
                    var transaction = WireFrame.Make(FrameKindEnum.Transaction, Protocol.EndsABody, correlationId: 0, passed.StreamSequence + 1, TransactionPayload.Make(end.End, effects));

                    added = 1;

                    return [passed.WithPayload((end with { Start = end.End - 1 }).ToBytes()), transaction];

                default:
                    return [passed];
            }
        });
        var child = LatticeDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound));

        var exception = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
    }

    // The effect that deletes the String key of MarkedRoot: its kind, the type id and the key, the length
    // of its UTF-8 and that UTF-8.
    private static byte[] DeleteOf(string code)
    {
        var key = Encoding.UTF8.GetBytes(code);
        var effect = new byte[1 + sizeof(uint) + sizeof(uint) + key.Length];

        effect[0] = TransactionPayload.Delete;
        BinaryPrimitives.WriteUInt32LittleEndian(effect.AsSpan(1), MarkedRoot.TypeId);
        BinaryPrimitives.WriteUInt32LittleEndian(effect.AsSpan(1 + sizeof(uint)), (uint)key.Length);
        key.CopyTo(effect, 1 + (2 * sizeof(uint)));

        return effect;
    }

    // The effect that writes the root as a load sends it: its kind, the length of the root and its bytes.
    private static byte[] WriteOf(byte[] root)
    {
        var effect = new byte[1 + sizeof(ulong) + root.Length];

        effect[0] = TransactionPayload.Write;
        BinaryPrimitives.WriteUInt64LittleEndian(effect.AsSpan(1), (ulong)root.Length);
        root.CopyTo(effect, 1 + sizeof(ulong));

        return effect;
    }
}
