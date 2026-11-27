using System.Buffers;
using Moltaro.Lattice.Engine.Child.Loading;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Loading;

// A direct receiver of valid wire roots and transactions. Its initial capture contains
// one root. The caller owns the arena and can inspect it after the receiver has returned.
internal sealed class ReceivedScratchFixture
{
    public const int FirstOversizedMember = 1_025;
    private const string Name = "nnnnnn";

    private readonly Arena _arena;
    private readonly RootTable _roots = new(new InstanceMemoryBudget(), pageLimit: null);
    private readonly RootLayout _layout;
    private readonly EntityTable _table;
    private readonly PositionClock _clock;
    private readonly ReceivedChanges _received;

    public ReceivedScratchFixture(Arena arena)
    {
        _arena = arena;
        var owner = new object();
        var epochs = new ReadEpochs(owner);
        var retired = new RetiredMemory(epochs, arena, _roots);
        _clock = new PositionClock(1, owner);
        var schema = PieceSchema.Schema;
        var type = schema.EntityTypes.Single(entity => entity.Name == "Piece");
        _layout = RootLayout.Compile(type, schema);
        _table = EntityTable.Open(type, schema, arena, _roots, epochs, retired, _clock, seed: 0);
        var tables = new EntityTables([_table]);
        var publisher = new ChangePublisher(arena, _roots, retired, _clock, tables);
        var writes = new Lock();
        _received = new ReceivedChanges(arena, _roots, retired, _clock, epochs, tables, publisher, writes, new CloseBarrier(owner),
            () => Assert.Fail("No publication fault was expected."),
            (count, member, _, _) =>
            {
                if (count == FirstOversizedMember - 1 && member == FirstOversizedMember)
                {
                    LargePreparations++;
                    if (PreparationFailure is { } failure)
                    {
                        throw failure;
                    }
                }
            },
            () =>
            {
                Assert.True(writes.IsHeldByCurrentThread);
                ScratchAllocations++;
                if (ScratchFailure is { } failure)
                {
                    ScratchFailure = null;
                    throw failure;
                }
            });

        var seed = new SequenceReader<byte>(Encode(0));
        _received.Place(_table, lastMutation: 1, member: 0, seed, received: 0);
        _received.EndLoad(captureStart: 1);
        _received.ValidateUniqueIndexes();
        ExpectState(1, members: 0);
        ScratchAllocations = 0;
    }

    public OutOfMemoryException? PreparationFailure { get; set; }
    public OutOfMemoryException? ScratchFailure { get; set; }
    public int LargePreparations { get; private set; }
    public int ScratchAllocations { get; private set; }

    public void ApplyBatch(ulong sequence, int members, int finalBinaryLength = 0)
    {
        var effects = new ArrayBufferWriter<byte>();
        for (var key = 1; key <= members; key++)
        {
            var data = key == members && finalBinaryLength != 0 ? new byte[finalBinaryLength] : null;
            WriteEffect(effects, sequence, (uint)key, Encode(key, data));
        }

        _received.Apply(sequence, new ReadOnlySequence<byte>(effects.WrittenMemory), CancellationToken.None);
    }

    public void ApplyOne(ulong sequence, long key)
    {
        var effects = new ArrayBufferWriter<byte>();
        WriteEffect(effects, sequence, member: 1, Encode(key));
        _received.Apply(sequence, new ReadOnlySequence<byte>(effects.WrittenMemory), CancellationToken.None);
    }

    public void ExpectState(ulong sequence, int members, int? followUp = null)
    {
        var count = 1 + members + (followUp.HasValue ? 1 : 0);
        Assert.Equal(sequence, _clock.Sequence);
        Assert.Equal(count, _table.Count);
        Assert.Equal(count, _roots.Count);
        Assert.Equal(0, _arena.Counters.PreparedBytes);
        Assert.Equal(0, _arena.Counters.RetiredBytes);

        ExpectPiece(0, mutation: 1);
        for (var key = 1; key <= members; key++)
        {
            ExpectPiece(key, mutation: 2);
        }

        if (followUp is { } following)
        {
            ExpectPiece(following, mutation: sequence);
        }
    }

    private void ExpectPiece(long key, ulong mutation)
    {
        Assert.True(_table.TryFind(EntityKey.OfInteger(LatticeFieldTypeEnum.Int64, key), out var handle, out var root));
        Assert.Equal(mutation, _roots.Entry(handle).LastMutationSequence);
        var reader = new RootReader(_arena, _layout, root);
        Assert.Equal(key, reader.GetInteger(_layout.EntityType.Key));
        Assert.Equal(Name, reader.GetString(_layout.EntityType.GetField("Name")));
        Assert.Equal(key, reader.GetInteger(_layout.EntityType.GetField("Count")));
        Assert.True(reader.IsNull(_layout.EntityType.GetField("Data")));
    }

    private ReadOnlySequence<byte> Encode(long key, byte[]? data = null) =>
        RootValues.Export(_layout, _layout.EntityType, [key, Name, null, data, (int)key]);

    private void WriteEffect(ArrayBufferWriter<byte> effects, ulong sequence, uint member, ReadOnlySequence<byte> root)
    {
        TransactionFormat.WriteEnvelopeOfWrite(effects.GetSpan(TransactionFormat.WriteEnvelopeSize), RootEnvelope.Size + root.Length);
        effects.Advance(TransactionFormat.WriteEnvelopeSize);
        RootEnvelope.Write(effects, (uint)_layout.EntityType.Id, sequence, member);
        foreach (var part in root)
        {
            effects.Write(part.Span);
        }
    }
}
