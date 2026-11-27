using System.Buffers.Binary;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Child.Following;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

public sealed class ChildReceiveBudgetTests
{
    private const long ChildLimit = 4L * Segment;
    private const int BinaryLength = 170_000;

    [Theory(Skip = Pending)]
    [InlineData(0, "resume")]
    [InlineData(1, "resume")]
    [InlineData(2, "resume")]
    [InlineData(1, "close")]
    [InlineData(1, "disconnect")]
    public async Task A_valid_frame_waits_for_its_backing_and_keeps_the_real_replies_and_application_in_order(
        int acceptedFrames, string end)
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true);
        var source = master.Set<PieceSchema.Piece>();
        var readiness = await source.InsertAsync(PieceSchema.Of(1, 8));
        var lowerBound = await ShortRequestRoomAsync(master.ListenUri!, PieceSchema.Of(1, 21));
        var pieceBytes = ManagedObjectSize(new byte[TransferBuffer.PieceSize]);
        var shortRoom = checked((int)(2 * lowerBound));
        Assert.InRange(shortRoom, lowerBound + 32, pieceBytes - 1);
        ulong blocked = PositionSequence.Of(readiness.Position) + 1;
        await using var relay = FrameRelay.Start(master.ListenUri!);

        var allocator = new CountingNativeAllocator();
        var paused = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? held = null;
        (byte[] Receive, byte[][] Pieces, byte[] Prefix)? atFrame = null;
        var attempts = 0;
        var options = new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = relay.Address,
            MemoryLimit = ChildLimit,
            SynchronizationTimeout = ReplicationNodes.BeyondTheBound,
        };
        EngineDatabase? receiving = null;
        await using var child = EngineDatabase.Create(options, new EngineTestInputs
        {
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            BeforeChildTransactionFrame = (sequence, received) =>
            {
                if (sequence == blocked && received == acceptedFrames)
                {
                    Interlocked.Increment(ref attempts);
                    if (held is null)
                    {
                        var node = receiving!;
                        var room = ChildLimit - ExpectCapacity(node, allocator).ChargedBytes;
                        atFrame = ReceiveBackings(node);
                        held = node.HoldBudgetBacking(checked((int)room - shortRoom));
                        Assert.InRange(ChildLimit - node.InspectBudget().ChargedBytes, lowerBound, pieceBytes - 1);
                    }
                }
            },
            ObserveChildPressure = pressure => paused.TrySetResult(pressure),
        });
        receiving = child;
        await child.OpenAsync().AsTask().WaitAsync(Bound);
        var local = child.Set<PieceSchema.Piece>();
        Assert.Equal(readiness.Position, child.Position);

        try
        {
            var first = local.InsertAsync(Pieces.Versioned(2, version: 2, BinaryLength)).AsTask();
            var pause = await paused.Task.WaitAsync(Bound);
            Assert.Equal(new ChildPressureSample(blocked, LatticeLimitEnum.Memory, true, acceptedFrames), pause);
            var second = local.InsertAsync(PieceSchema.Of(3, 21)).AsTask();
            await Eventually.HoldsAsync(() => source.Get(3L) is not null, "The master did not finish a later forwarded write.");
            IReadOnlyList<WireFrame> passed = [];
            await Eventually.HoldsAsync(() =>
            {
                // Both replies can arrive before the last frame of the first transaction.
                var snapshot = relay.ToChild;
                var frames = snapshot.Where(frame => frame.Kind == FrameKindEnum.Transaction).Take(3).ToArray();
                if (snapshot.Count(frame => frame.Kind == FrameKindEnum.WriteReply) < 2 || frames.Length != 3 || !frames[2].EndsABody)
                {
                    return false;
                }

                passed = snapshot;
                return true;
            }, "The master did not pass the two actual replies and the whole three-frame transaction.");
            var transaction = passed.Where(frame => frame.Kind == FrameKindEnum.Transaction).ToArray();
            Assert.True(transaction.Length >= 3);
            Assert.Equal(blocked, TransactionPayload.SequenceOf(transaction[0]));
            Assert.False(transaction[0].EndsABody);
            Assert.False(transaction[1].EndsABody);
            Assert.True(transaction[2].EndsABody);
            var replies = passed.Where(frame => frame.Kind == FrameKindEnum.WriteReply).ToArray();
            Assert.Single(passed.TakeWhile(frame => !ReferenceEquals(frame, transaction[acceptedFrames])),
                frame => frame.Kind == FrameKindEnum.WriteReply);
            Assert.NotEqual(replies[0].CorrelationId, replies[1].CorrelationId);
            var prepared = Assert.IsType<ValueTuple<byte[], byte[][], byte[]>>(atFrame);
            var retained = ReceiveBackings(child);
            Assert.Same(prepared.Item1, retained.Receive);
            Assert.Equal(prepared.Item2.Length, retained.Pieces.Length);
            for (var piece = 0; piece < retained.Pieces.Length; piece++)
            {
                Assert.Same(prepared.Item2[piece], retained.Pieces[piece]);
                Assert.NotSame(retained.Receive, retained.Pieces[piece]);
            }

            Assert.Equal(retained.Pieces.Length, retained.Pieces.Distinct(ReferenceEqualityComparer.Instance).Count());
            Assert.True(retained.Receive.AsSpan().IndexOf(transaction[acceptedFrames].Bytes) >= 0);
            var prefix = transaction.Take(acceptedFrames)
                .SelectMany((frame, index) => index == 0 ? frame.Payload[TransactionPayload.HeaderSize..] : frame.Payload).ToArray();
            Assert.Equal(prefix, prepared.Item3);
            Assert.Equal(prefix, retained.Prefix);

            const int laterBytes = 2 * 1024 * 1024 + 64;
            var later = await source.InsertAsync(PieceSchema.Of(4, 8, data: laterBytes)).AsTask().WaitAsync(Bound);
            var ownedTail = await MaintenanceStep.RunAfterAdmissionAsync(master, parts =>
            {
                var owners = new HashSet<DeliveryBlock>(ReferenceEqualityComparer.Instance);
                for (var block = Backing<DeliveryBlock?>(parts.Delivery, "_first"); block is not null; block = block.Next)
                {
                    Assert.True(owners.Add(block));
                }

                if (Backing<DeliveryBlock?>(parts.Delivery, "_spare") is { } spare)
                {
                    Assert.True(owners.Add(spare));
                }

                Assert.True(owners.Count >= 2);
                return owners.ToArray();
            });
            var physical = ownedTail.Sum(block => ManagedObjectSize(block.Bytes));
            var masterHeld = master.InspectHeld();
            Assert.Equal(physical, masterHeld.DeliveryCapacityBytes);
            Assert.DoesNotContain(masterHeld.Arrays, array =>
                array.ElementType == typeof(byte) && ownedTail.Any(block => block.Bytes.Length == array.Length));
            ExpectCapacity(master, masterAllocator);
            var logical = master.Counters.PendingDeliveryBytes;
            Assert.True(logical > 2L * 1024 * 1024);
            Assert.True(physical > logical);
            Assert.Equal(readiness.Position, child.Position);
            Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
            Assert.Equal(8, local.Get(1L)?.Name.Length);
            Assert.Null(local.Get(2L));
            Assert.Null(local.Get(3L));
            Assert.Null(local.Get(4L));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.DoesNotContain(Acknowledged(relay), sequence => sequence >= blocked);

            using var cancellation = new CancellationTokenSource();
            var wait = child.WaitForPositionAsync(later.Position, ReplicationNodes.BeyondTheBound, cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(Bound));
            Assert.Equal(readiness.Position, child.Position);

            // No clock or release signal is provided during this interval. A retry loop
            // must leave room for the reader and sender and may not hammer the blocked frame.
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.InRange(Volatile.Read(ref attempts), 1, 128);
            if (end == "close")
            {
                await child.CloseAsync().AsTask().WaitAsync(Bound);
                var known = await first.WaitAsync(Bound);
                Assert.Equal(blocked, PositionSequence.Of(known.Position));
                var unknown = await Assert.ThrowsAsync<LatticeException>(() => second.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, unknown.Error);
                Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
                allocator.ExpectEverythingReturned();
                await relay.ChildEndedAsync();
                await NothingRegistered.ExpectAsync(master, 100);
                return;
            }
            if (end == "disconnect")
            {
                var beforeEnd = child.InspectBudget();
                var followingTask = Backing<Task>(child, "_following");
                relay.Cut();
                // The sender observes the broken connection independently of the held receive body.
                // The contract allows idle receive to notice a disconnect only when it next reads.
                var signal = local.InsertAsync(PieceSchema.Of(20, 8)).AsTask();
                await followingTask.WaitAsync(Bound);
                _ = await Assert.ThrowsAsync<LatticeException>(() => signal.WaitAsync(Bound));
                var known = await Assert.ThrowsAsync<LatticeException>(() => first.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, known.Error);
                Assert.Equal(blocked, PositionSequence.Of(known.CommittedPosition!.Value));
                var unknown = await Assert.ThrowsAsync<LatticeException>(() => second.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, unknown.Error);
                Assert.True(beforeEnd.ChargedBytes - ExpectCapacity(child, allocator).ChargedBytes >= 64 * 1024);
                Assert.Equal(readiness.Position, child.Position);
                Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
                Assert.Null(local.Get(2L));
                await NothingRegistered.ExpectAsync(master, 100);
                return;
            }

            held!.Dispose();
            var admitted = await first.WaitAsync(Bound);
            var following = await second.WaitAsync(Bound);
            await child.WaitForPositionAsync(later.Position, Bound);
            Assert.Equal(blocked, PositionSequence.Of(admitted.Position));
            Assert.Equal(blocked + 1, PositionSequence.Of(following.Position));
            Assert.Equal(BinaryLength, local.Get(2L)?.Data?.Length);
            Assert.Equal(21, local.Get(3L)?.Name.Length);
            Assert.Equal(laterBytes, local.Get(4L)?.Data?.Length);
            Assert.Equal(later.Position, child.Position);
            Assert.Equal(4, local.Enumerate().Count());
            await Eventually.HoldsAsync(() => Acknowledged(relay).Contains(PositionSequence.Of(later.Position)),
                "The child did not acknowledge the resumed prefix.");
            await Eventually.HoldsAsync(() => master.Counters.PendingDeliveryBytes == 0, "The master did not dequeue the applied transactions.");
            ExpectCapacity(child, allocator);
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task The_next_message_can_reuse_its_existing_pieces_when_one_new_piece_would_not_fit()
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true);
        var source = master.Set<PieceSchema.Piece>();
        var prefix = await source.InsertAsync(PieceSchema.Of(1, 8));
        var second = PositionSequence.Of(prefix.Position) + 2;
        var allocator = new CountingNativeAllocator();
        var readyToPrepare = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedPressure = false;
        IDisposable? held = null;
        EngineDatabase? receiving = null;
        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, ChildLimit),
            new EngineTestInputs
            {
                Allocator = allocator,
                DisableBackgroundMaintenance = true,
                BeforeChildTransactionFrame = (sequence, frame) =>
                {
                    if (sequence == second && frame == 0 && held is null)
                    {
                        var node = receiving!;
                        held = node.HoldBudgetBacking(checked((int)(ChildLimit - node.InspectBudget().ChargedBytes - 1_024)));
                    }
                },
                BeforeChildPreparationMember = _ =>
                {
                    if (held is not null)
                    {
                        readyToPrepare.TrySetResult();
                    }
                },
                ObserveChildPressure = pressure => receivedPressure |= pressure.DuringReceive,
            });
        receiving = child;
        await child.OpenAsync().AsTask().WaitAsync(Bound);
        var seed = await source.InsertAsync(PieceSchema.Of(2, 8, data: BinaryLength));
        await child.WaitForPositionAsync(seed.Position, Bound);
        var pieces = PiecesOf(child);
        Assert.True(pieces.Length >= 3);
        ExpectCapacity(child, allocator);
        try
        {
            var repeat = await source.InsertAsync(PieceSchema.Of(3, 8, data: BinaryLength));
            await readyToPrepare.Task.WaitAsync(Bound);
            Assert.False(receivedPressure);
            Assert.Equal(pieces, PiecesOf(child));
            ExpectCapacity(child, allocator);
            held!.Dispose();
            await child.WaitForPositionAsync(repeat.Position, Bound);
            Assert.Equal(BinaryLength, child.Set<PieceSchema.Piece>().Get(3L)?.Data?.Length);
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_oversized_valid_announcement_pauses_and_an_invalid_announcement_ends_the_connection(bool invalid)
    {
        const long limit = 6L * 1024 * 1024;
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true);
        var source = master.Set<PieceSchema.Piece>();
        var initial = await source.InsertAsync(PieceSchema.Of(1, 8));
        var sequence = PositionSequence.Of(initial.Position) + 1;
        var first = true;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (!invalid || !first || frame.Kind != FrameKindEnum.Transaction)
            {
                return [frame];
            }

            first = false;
            var payload = frame.Payload;
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(sizeof(ulong)), 0);
            return [frame.WithPayload(payload)];
        });
        var allocator = new CountingNativeAllocator();
        var paused = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, limit),
            new EngineTestInputs { Allocator = allocator, DisableBackgroundMaintenance = true, ObserveChildPressure = pressure => paused.TrySetResult(pressure) });
        var before = ExpectCapacity(child, allocator);
        var announced = await source.InsertAsync(PieceSchema.Of(2, 8, data: 8 * 1024 * 1024)).AsTask().WaitAsync(Bound);
        Assert.Equal(sequence, PositionSequence.Of(announced.Position));

        if (invalid)
        {
            await relay.ChildEndedAsync();
            Assert.False(paused.Task.IsCompleted);
        }
        else
        {
            Assert.Equal(new ChildPressureSample(sequence, LatticeLimitEnum.Memory, true, 0), await paused.Task.WaitAsync(Bound));
            var original = relay.FromMaster.First(frame => frame.Kind == FrameKindEnum.Transaction);
            Assert.True(TransactionPayload.AnnouncedLengthOf(original) > (ulong)limit);
            Assert.Equal(before.ChargedBytes, ExpectCapacity(child, allocator).ChargedBytes);
            Assert.Equal(before.NativeBytes, allocator.OutstandingBytes);
        }

        Assert.Equal(initial.Position, child.Position);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(2L));
        Assert.DoesNotContain(Acknowledged(relay), applied => applied >= sequence);
        await child.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    internal static ulong[] Acknowledged(FrameRelay relay) =>
        [.. relay.FromChild.Where(frame => frame.Kind == FrameKindEnum.Applied)
            .Select(frame => BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload))];

    internal static async Task<long> ShortRequestRoomAsync<TEntity>(Uri master, TEntity existing)
        where TEntity : class
    {
        var hasReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var relay = FrameRelay.Start(master, frame =>
        {
            if (frame.Kind == FrameKindEnum.WriteReply)
            {
                hasReply.TrySetResult();
                release.Task.Wait(Bound);
            }

            return [frame];
        });
        var allocator = new CountingNativeAllocator();
        await using var control = await ReplicationNodes.OpenChildAsync(relay.Address, new EngineTestInputs
        {
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
        });
        var set = control.Set<TEntity>();
        // The duplicate check happens on the master. This sends a complete short
        // request without changing the real master's starting position. Size the
        // owned forwarding peak, which excludes transient CLR/JIT allocations.
        var before = ExpectCapacity(control, allocator);
        control.ResetBudgetPeak();
        var writing = set.InsertAsync(existing).AsTask();
        long forwardingBytes;
        try
        {
            await hasReply.Task.WaitAsync(Bound);
            Assert.False(writing.IsCompleted);
            var held = ExpectCapacity(control, allocator);
            forwardingBytes = held.PeakBytes - before.ChargedBytes;
        }
        finally
        {
            release.TrySetResult();
        }

        var reply = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.DuplicateKey, reply.Error);
        return forwardingBytes;
    }

    internal static (byte[] Receive, byte[][] Pieces, byte[] Prefix) ReceiveBackings(EngineDatabase child)
    {
        var follower = Backing<Follower>(child, "_follower");
        var connection = Backing<object>(Backing<object>(follower, "_link"), "_connection");
        var receive = Backing<byte[]>(connection, "_received");
        var pieces = PiecesOf(child).Cast<TransferPiece>().ToArray();
        return (receive, pieces.Select(piece => piece.Bytes).ToArray(),
            pieces.SelectMany(piece => piece.Bytes.Take(piece.Used)).ToArray());
    }

    private static object[] PiecesOf(EngineDatabase child)
    {
        var follower = Backing<Follower>(child, "_follower");
        var transfer = Backing<object>(Backing<object>(follower, "_assembler"), "_large");
        return Backing<System.Collections.IList>(transfer, "_pieces").Cast<object>().ToArray();
    }
}
