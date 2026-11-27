using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine.Positions;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Fixture;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// Sparse committed effects use the actual predecessor and the existing transaction framing
// (replication-and-hosting.md, "How are live changes delivered?").
public sealed class SparseUpdateTests(ITestOutputHelper output)
{
    [Fact]
    public async Task One_fixture_field_emits_fewer_effect_bytes_than_ordinary_replacement()
    {
        var baseline = GeneratedRecords.Of(FixtureForms.Full, "User")[0];
        var schema = GeneratedRecords.Of(FixtureForms.Full).Schema;
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        var users = master.Set("User");
        await users.InsertAsync(baseline);
        var id = baseline.Get<long>("Id");
        var active = baseline.Get<bool>("IsActive");
        var baseSequence = PositionSequence.Of(master.Position);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var update = users.CreateUpdate(id);
        update.Assign("IsActive", !active);
        var sparse = await update.CommitAsync();
        await child.WaitForPositionAsync(sparse.Position, Bound);
        Assert.Equal(!active, child.Set("User").Get(id)!.Get<bool>("IsActive"));

        var replacement = users.Get(id)!;
        await users.ReplaceAsync(baseline);
        Assert.Equal(active, users.Get(id)!.Get<bool>("IsActive"));
        var full = await users.ReplaceAsync(replacement);
        await child.WaitForPositionAsync(full.Position, Bound);
        Assert.Equal(!active, child.Set("User").Get(id)!.Get<bool>("IsActive"));
        Assert.Equal(7, Protocol.WireFormat);

        var sparseFrame = TransactionPayload.SingleFrameAt(relay, sparse.Position);
        var fullFrame = TransactionPayload.SingleFrameAt(relay, full.Position);
        var sparseBytes = TransactionPayload.EffectsOf(sparseFrame);
        var fullBytes = TransactionPayload.EffectsOf(fullFrame);
        Assert.Equal((ulong)sparseBytes.Length, TransactionPayload.AnnouncedLengthOf(sparseFrame));
        Assert.Equal((ulong)fullBytes.Length, TransactionPayload.AnnouncedLengthOf(fullFrame));
        Assert.Equal(baseSequence, BinaryPrimitives.ReadUInt64LittleEndian(sparseBytes.AsSpan(SparseUpdatePayload.BaseSequenceAt)));
        Assert.Equal(TransactionPayload.Write, fullBytes[0]);
        var sizes = $"Partial effect: {sparseBytes.Length} bytes; whole replacement: {fullBytes.Length} bytes.";
        output.WriteLine(sizes);
        Assert.True(sparseBytes.Length < fullBytes.Length, sizes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_live_base_divergence_faults_only_if_every_later_effect_is_structurally_valid(bool malformedTail)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var items = master.Set<UpdateItem>();
        await items.InsertAsync(UpdateSchema.Item("ada"));
        var baseline = UpdateSchema.Item("ada");
        baseline.Login = "original";
        await items.ReplaceAsync(baseline);
        var before = master.Position;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction)
            {
                return [frame];
            }

            var effects = TransactionPayload.EffectsOf(frame);
            Assert.Equal(TransactionPayload.Write, effects[0]);
            var firstLength = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(effects.AsSpan(1)));
            var sparseOffset = 1 + sizeof(ulong) + firstLength;
            BinaryPrimitives.WriteUInt64LittleEndian(effects.AsSpan(sparseOffset + SparseUpdatePayload.BaseSequenceAt), 1);
            return [frame.WithPayload(TransactionPayload.Make(TransactionPayload.SequenceOf(frame), TransactionPayload.CountOf(frame),
                malformedTail ? [.. effects, byte.MaxValue] : effects))];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var clock = ObservedClock.Of(child);

        var update = items.CreateUpdate("ada");
        update.Assign("Login", "new");
        var batch = master.CreateWriteBatch();
        batch.Insert(UpdateSchema.Item("grace"));
        batch.Update(update);
        var result = await batch.CommitAsync();
        await relay.ChildEndedAsync();

        Assert.Contains(relay.ToChild, frame => frame.Kind == FrameKindEnum.Transaction);
        Assert.Equal("new", items.Get("ada")!.Login);
        Assert.Equal(before, clock.Position);
        if (malformedTail)
        {
            Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
            Assert.Equal(before, child.Position);
            Assert.Equal("original", child.Set<UpdateItem>().Get("ada")!.Login);
            Assert.Null(child.Set<UpdateItem>().Get("grace"));
        }
        else
        {
            Assert.Equal(LatticeDatabaseStateEnum.Faulted, child.State);
            var error = await Assert.ThrowsAsync<LatticeException>(() => child.WaitForPositionAsync(result.Position, Bound).AsTask());
            Assert.Equal(LatticeErrorEnum.Unavailable, error.Error);
        }
    }

    [Fact]
    public async Task An_impossible_resolved_Set_insertion_faults_a_live_child_even_with_the_exact_base_sequence()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var items = master.Set<UpdateItem>();
        await items.InsertAsync(UpdateSchema.Item("ada"));

        await using var other = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var different = UpdateSchema.Item("ada");
        different.Tags!.Add("friend");
        await other.Set<UpdateItem>().InsertAsync(different);
        await using var otherLink = FrameRelay.Start(other.ListenUri!);
        await using var witness = await ReplicationNodes.OpenChildAsync(otherLink.Address);
        var differentRoot = Assert.Single(otherLink.FromMaster, frame => frame.Kind == FrameKindEnum.Blocks).Payload;

        await using var relay = FrameRelay.Start(master.ListenUri!, frame => frame.Kind == FrameKindEnum.Blocks
            ? [frame.WithPayload(differentRoot)]
            : [frame]);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var clock = ObservedClock.Of(child);
        var before = child.Position;
        Assert.Equal(master.Position, before);
        Assert.Contains("friend", child.Set<UpdateItem>().Get("ada")!.Tags!);

        var update = items.CreateUpdate("ada");
        var added = update.SetAdd("Tags", "friend");
        var result = await update.CommitAsync();
        Assert.True(added.Changed);
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Faulted, child.State);
        Assert.Equal(before, clock.Position);
        var error = await Assert.ThrowsAsync<LatticeException>(() => child.WaitForPositionAsync(result.Position, Bound).AsTask());
        Assert.Equal(LatticeErrorEnum.Unavailable, error.Error);
    }
}
