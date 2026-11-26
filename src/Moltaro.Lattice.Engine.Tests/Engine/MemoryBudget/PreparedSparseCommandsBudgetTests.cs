using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Child.Following;
using Moltaro.Lattice.Engine.Child.Forwarding;
using Moltaro.Lattice.Engine.Children;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Updates;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Roots.Edits;
using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class PreparedSparseCommandsBudgetTests
{
    private const int Commands = 4_096;
    private const long Limit = 16L * Segment;
    private static readonly LatticeSchema OperandSchema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }, { 'Id': 3, 'Name': 'Instant', 'Type': 'DateTimeOffset' }");

    [Fact]
    public void Split_text_admits_its_exact_string_without_pooled_scratch_and_checks_bad_utf8_first()
    {
        var text = LongText();
        var wire = new byte[sizeof(uint) + Encoding.UTF8.GetByteCount(text)];
        BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)(wire.Length - sizeof(uint)));
        Encoding.UTF8.GetBytes(text, wire.AsSpan(sizeof(uint)));
        Assert.Equal(0xF0, wire[TransferBuffer.PieceSize - 1]);
        Assert.InRange(wire[TransferBuffer.PieceSize], 0x80, 0xBF);

        Assert.True(OperandSchema.TryGetEntityType(ItemSchema.EntityTypeName, out var type));
        var layout = StructureLayout.OfRoot(type, []);
        var position = layout.PositionOf([type.GetField("Text")]);
        using var transfer = new TransferBuffer();
        var budget = new Budgeting.MemoryBudget(observesBackings: true);
        var bytes = ManagedObjectSize(text);
        budget.Open(Segment);
        using var lease = MemoryLease.Create(budget);
        var marker = lease.Allocate<byte>(1, nameof(PreparedSparseCommandsBudgetTests));
        var prepaid = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var opening = budget.Inspect((_, owner) => prepaid.Add(owner));
        Assert.Contains(marker, prepaid);
        var baseline = prepaid.Sum(ManagedObjectSize);
        Assert.Equal((baseline, baseline, 0L), opening);
        budget.Open(baseline + bytes - 1);
        transfer.Write(wire);
        var noRoom = Assert.Throws<BudgetRefusedException>(() => Read());
        Assert.Equal(LatticeLimitEnum.Memory, noRoom.Limit);
        Assert.Equal((baseline, baseline, 0L), budget.Inspect(null));

        budget.Open(baseline);
        var goodContinuation = wire[TransferBuffer.PieceSize];
        wire[TransferBuffer.PieceSize] = (byte)'(';
        Reload(wire);
        Assert.Equal(LatticeErrorEnum.ValidationFailed, Assert.Throws<LatticeException>(() => Read()).Error);
        wire[TransferBuffer.PieceSize] = goodContinuation;
        BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)(wire.Length - sizeof(uint) - 1));
        Reload(wire.AsSpan(0, wire.Length - 1));
        Assert.Equal(LatticeErrorEnum.ValidationFailed, Assert.Throws<LatticeException>(() => Read()).Error);
        Assert.Equal((baseline, baseline, 0L), budget.Inspect(null));

        BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)(wire.Length - sizeof(uint)));
        Reload(wire);
        budget.Open(baseline + bytes);
        using var trace = new DecodePoolTrace();
        var decoded = Read();
        Assert.Equal(0, trace.LargestRent);
        Assert.NotSame(text, decoded);
        Assert.Equal(text, decoded);
        var owners = new List<object>();
        Assert.Equal((baseline + bytes, baseline + bytes, 0L), budget.Inspect((_, owner) => owners.Add(owner)));
        Assert.Same(decoded, Assert.Single(owners.Except(prepaid, ReferenceEqualityComparer.Instance)));
        lease.Dispose();
        Assert.Equal((0L, baseline + bytes, 0L), budget.Inspect(null));

        string Read()
        {
            var sequence = transfer.Sequence;
            Assert.False(sequence.IsSingleSegment);
            var reader = new SequenceReader<byte>(sequence);
            var result = UpdateOperands.Read(ref reader, type, layout, position, lease);
            Assert.True(reader.End);
            return Assert.IsType<string>(result);
        }

        void Reload(ReadOnlySpan<byte> value)
        {
            transfer.Clear();
            transfer.Write(value);
        }
    }

    [Fact]
    public async Task A_multiframe_sparse_write_owns_the_decoded_text_and_full_timestamp_box()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var master = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = OperandSchema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            ListenUri = MasterAndChild.Loopback,
            MemoryLimit = Limit,
        }, new EngineTestInputs { ObserveBudgetBackings = true, Allocator = allocator, DisableBackgroundMaintenance = true, Persistence = storage });
        await master.OpenAsync();
        var source = master.Set(ItemSchema.EntityTypeName);
        var seed = await source.InsertAsync(new LatticeRecord(source.EntityType)
        {
            ["Id"] = 1L,
            ["Text"] = "before",
            ["Instant"] = DateTimeOffset.UnixEpoch,
        });
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var local = child.Set(ItemSchema.EntityTypeName);
        var text = LongText();
        var instant = DateTimeOffset.UnixEpoch.AddHours(29);
        var update = local.CreateUpdate(1L);
        update.Assign("Text", text);
        update.Assign("Instant", instant);
        storage.Hold();
        var done = update.CommitAsync().AsTask();
        try
        {
            await storage.NextAsync();
            var commands = await MaintenanceStep.RunAfterAdmissionAsync(master, parts =>
                Assert.Single(Backing<CommitGroup>(parts.Coordinator, "_group").Writes).Write.Update!.Commands);
            var heldText = Assert.IsType<string>(commands[0].First);
            var timestampBox = commands[1].First!;
            Assert.NotSame(text, heldText);
            Assert.Equal(text, heldText);
            Assert.Equal(instant, Assert.IsType<DateTimeOffset>(timestampBox));
            Assert.Equal(32, ManagedObjectSize(timestampBox));
            _ = ExpectCapacity(master, allocator, out _, heldText, timestampBox);
            Assert.Equal(seed.Position, master.Position);
            Assert.Equal(seed.Position, child.Position);
            Assert.True(relay.FromChild.Count(frame => frame.Kind == FrameKindEnum.WriteRequest) > 1);

            storage.ReleaseAll();
            var published = await done.WaitAsync(Bound);
            Assert.Equal(published.Position, child.Position);
            Assert.Equal(published.Position, master.Position);
            Assert.True(relay.FromMaster.Count(frame => frame.Kind == FrameKindEnum.Transaction) > 1);
            Assert.Equal(text, local.Get(1L)!.Get<string>("Text"));
            Assert.Equal(instant, local.Get(1L)!.Get<DateTimeOffset>("Instant"));
            master.InspectBudget((_, backing) =>
            {
                Assert.NotSame(heldText, backing);
                Assert.NotSame(timestampBox, backing);
            });
            _ = ExpectCapacity(master, allocator);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    private static string LongText() => string.Concat(Enumerable.Repeat("a\U0001F600", 50_000));

    private sealed class DecodePoolTrace : EventListener
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;

        public int LargestRent { get; private set; }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource")
            {
                EnableEvents(source, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (Environment.CurrentManagedThreadId == _thread && data.EventName == "BufferRented")
            {
                LargestRent = Math.Max(LargestRent, (int)data.Payload![1]!);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forwarded_outcome_keeps_its_actual_bitmap_until_sender_take_or_close(bool closes)
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        var received = new TaskCompletionSource<RequestReceiver>(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseApply = new ManualResetEventSlim();
        var pressure = new TaskCompletionSource<ChildPressureSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        var armApply = 0;
        await using var master = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = PieceSchema.Schema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            ListenUri = MasterAndChild.Loopback,
            MemoryLimit = Limit,
        }, new EngineTestInputs
        {
            ObserveBudgetBackings = true,
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            Persistence = storage,
            ObserveMasterRequestFrame = receiver => received.TrySetResult(receiver),
            BeforeMasterReplyDrain = async stop =>
            {
                beforeDrain.TrySetResult();
                await release.Task.WaitAsync(Bound, stop);
            },
        });
        await master.OpenAsync();
        var source = master.Set("Holder");
        var initialRoot = Empty(source, 1L);
        initialRoot["Lines"] = new Dictionary<string, string> { ["0"] = "before" };
        var seed = await source.InsertAsync(initialRoot);
        await using var relay = FrameRelay.Start(master.ListenUri!);
        var childAllocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(
            ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound, Limit), new EngineTestInputs
            {
                ObserveBudgetBackings = true,
                Allocator = childAllocator,
                DisableBackgroundMaintenance = true,
                BeforeChildPreparationMember = _ =>
                {
                    if (Interlocked.Exchange(ref armApply, 0) != 0)
                    {
                        beforeApply.TrySetResult();
                        Assert.True(releaseApply.Wait(Bound));
                    }
                },
                ObserveChildPressure = sample => pressure.TrySetResult(sample),
            });
        var local = child.Set("Holder");
        if (!closes)
        {
            var initial = ExpectCapacity(child, childAllocator);
            using (LeaveRoom(child, initial, 40))
            {
                var held = ExpectCapacity(child, childAllocator);
                await RefusedAsync(Update(local, 1L).CommitAsync().AsTask(), LatticeLimitEnum.Memory);
                Assert.Equal(held.ChargedBytes, ExpectCapacity(child, childAllocator).ChargedBytes);
                Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == FrameKindEnum.WriteRequest);
                Assert.Equal(seed.Position, master.Position);
            }

            Assert.Equal(initial.ChargedBytes, ExpectCapacity(child, childAllocator).ChargedBytes);
        }

        ResolvedCommand[][]? overlapping = null;
        if (!closes)
        {
            allocator.HoldNextAllocation(() =>
            {
                var actual = new List<ResolvedCommand[]>();
                master.InspectBudget((owner, backing) =>
                {
                    if (owner is nameof(RootUpdate) or nameof(PreparedUpdates) && backing is ResolvedCommand[] commands)
                    {
                        actual.Add(commands);
                    }
                });

                overlapping = [.. actual];
            });
        }

        storage.Hold();
        var value = closes ? "value" : new string('x', 1_024);
        var sending = Update(local, 1L, value).CommitAsync().AsTask();
        try
        {
            var receiver = await received.Task.WaitAsync(Bound);
            await storage.NextAsync();
            var write = await MaintenanceStep.RunAfterAdmissionAsync(master, parts =>
                Assert.Single(Backing<CommitGroup>(parts.Coordinator, "_group").Writes));
            var input = Assert.IsType<UpdateInput>(write.Write.Update);
            var sourceBits = Backing<byte[]>(input.Outcome, "_bits");
            var effect = write.Updates!.EffectOf(0)!.Value;
            var canonical = Assert.IsType<string>(effect.Commands[0].Command.First);
            var decoded = input.Commands.SelectMany(command => new[] { command.First, command.Second })
                .OfType<string>().ToHashSet(ReferenceEqualityComparer.Instance);
            Assert.Equal(2 * Commands, decoded.Count);
            Assert.Equal("0", canonical);
            Assert.DoesNotContain(canonical, decoded, ReferenceEqualityComparer.Instance);
            Assert.Equal(Commands / 8, sourceBits.Length);
            _ = ExpectCapacity(master, allocator, out _, input, input.Commands, sourceBits, canonical, effect.Commands);
            master.InspectBudget((_, backing) =>
            {
                if (backing is string text)
                {
                    decoded.Remove(text);
                }
            });
            Assert.Empty(decoded);
            if (!closes)
            {
                Assert.NotNull(overlapping);
                Assert.Equal(2, overlapping.Length);
                Assert.Contains(effect.Commands, overlapping, ReferenceEqualityComparer.Instance);
                Assert.All(overlapping, array => Assert.True(ManagedObjectSize(array) >= ManagedObjectSize(effect.Commands)));
            }

            Assert.Equal(seed.Position, master.Position);
            storage.ReleaseAll();
            var replies = Backing<RecordedReplies>(receiver, "_replies");
            var pending = Backing<Queue<RecordedReply>>(replies, "_replies");
            await beforeDrain.Task.WaitAsync(Bound);
            var recorded = Assert.Single(pending);
            Assert.Equal(Commands, recorded.Updates.Count);
            var bitmap = Assert.IsType<byte[]>(recorded.Updates.Bits);
            Assert.Equal(Commands / 8, bitmap.Length);
            Assert.All(bitmap, word => Assert.Equal(byte.MaxValue, word));
            _ = ExpectCapacity(master, allocator, out _, bitmap, Backing<Array>(pending, "_array"));
            Assert.Equal(seed.Position, child.Position);
            Assert.Equal(recorded.Result.Position, master.Position);
            Assert.DoesNotContain(relay.ToChild, frame => frame.Kind == FrameKindEnum.WriteReply);
            var follower = Backing<Follower>(child, "_follower");
            var calls = Backing<Dictionary<ulong, ForwardedCall>>(Backing<object>(follower, "_pending"), "_calls");
            var call = Assert.Single(calls).Value;
            var updates = Assert.IsType<UpdateReplies>(call.Updates);
            var sentBitmap = Assert.IsType<byte[]>(updates.ReceiveBuffer);
            var members = Backing<Array>(updates, "_updates");
            Assert.Single(members);
            Assert.Equal(bitmap.Length, sentBitmap.Length);
            Assert.All(sentBitmap, word => Assert.Equal((byte)0, word));
            _ = ExpectCapacity(child, childAllocator, out _, updates, sentBitmap, members);

            if (closes)
            {
                await child.CloseAsync().AsTask().WaitAsync(Bound);
                var failure = await Assert.ThrowsAsync<LatticeException>(() => sending.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, failure.Error);
            }
            else
            {
                Interlocked.Exchange(ref armApply, 1);
                release.TrySetResult();
                await beforeApply.Task.WaitAsync(Bound);
                Assert.Empty(calls);
                Assert.True(call.Outcome.IsCompletedSuccessfully);
                var delivered = await call.Outcome;
                Assert.Same(sentBitmap, delivered.Bits);
                Assert.Equal(bitmap, sentBitmap);
                // The classified frame is complete and the hook holds Writes. Its opening
                // limit is already known, so this observation must not enter Counters.
                var known = ExpectCapacity(child, childAllocator, Limit, out _, updates, sentBitmap, members);
                using (LeaveRoom(child, known, 40))
                {
                    Assert.Equal(40, Limit - child.InspectBudget().ChargedBytes);
                    releaseApply.Set();
                    var finished = await Task.WhenAny(pressure.Task, sending).WaitAsync(Bound);
                    if (finished == sending)
                    {
                        await sending;
                        Assert.Fail("The actual call completed before the expected local application pressure.");
                    }

                    var paused = await pressure.Task.WaitAsync(Bound);
                    Assert.Equal(LatticeLimitEnum.Memory, paused.Limit);
                    Assert.False(paused.DuringReceive);
                    Assert.Equal(recorded.Result.Position.Sequence, paused.Sequence);
                    Assert.Equal(seed.Position, child.Position);
                    Assert.False(sending.IsCompleted);
                    _ = ExpectCapacity(child, childAllocator, out _, updates, sentBitmap, members);
                }

                var applied = await sending.WaitAsync(Bound);
                Assert.Equal(recorded.Result.Position, applied.Position);
                Assert.True(applied.Changed);
                Assert.True(local.TryGet(1L, out _, out var revision));
                ExpectMap(local, 1L, Commands, revision, value);
            }

            await CountersWait.UntilAsync(master, _ => !StillCharged(), "the actual reply bitmap has no connection owner");
            Assert.Empty(pending);
            ExpectCapacity(master, allocator);
            child.InspectBudget((_, backing) =>
            {
                Assert.NotSame(updates, backing);
                Assert.NotSame(sentBitmap, backing);
                Assert.NotSame(members, backing);
            });
            Assert.True(source.TryGet(1L, out _, out var masterRevision));
            ExpectMap(source, 1L, Commands, masterRevision, value);

            bool StillCharged()
            {
                var charged = false;
                master.InspectBudget((_, backing) => charged |= ReferenceEquals(bitmap, backing));
                return charged;
            }
        }
        finally
        {
            storage.ReleaseAll();
            releaseApply.Set();
            release.TrySetResult();
        }
    }

    private static IDisposable LeaveRoom(EngineDatabase node, BudgetCapacitySample current, int bytes) =>
        node.HoldBudgetBacking(checked((int)(Limit - current.ChargedBytes - bytes - ManagedObjectSize(Array.Empty<byte>()))));

    [Fact]
    public async Task Sparse_command_backings_are_charged_until_publication_and_released_on_refusal_or_withdrawal()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(Limit, allocator, listen: true, persistence: storage);
        var items = node.Set("Holder");
        await items.InsertAsync(Empty(items, 1));
        var second = await items.InsertAsync(Empty(items, 2));
        Assert.True(items.TryGet(1L, out _, out var firstRevision));
        Assert.True(items.TryGet(2L, out _, out var unchanged));
        await using var child = await ReplicationNodes.OpenChildAsync(node.ListenUri!);

        try
        {
            storage.Hold();
            var publishing = Update(items, 1).CommitAsync().AsTask();
            await storage.NextAsync();
            var publicationOwners = await OwnersAsync(queued: false);
            var commandBytes = ManagedObjectSize(publicationOwners[^1]);
            var dictionaryBytes = publicationOwners.Take(publicationOwners.Length - 1).Sum(ManagedObjectSize);
            Assert.True(commandBytes > 128 * 1024);
            Assert.True(dictionaryBytes > 0);
            _ = ExpectCapacity(node, allocator, out _, publicationOwners);
            Assert.Equal(second.Position, node.Position);
            Assert.Equal(second.Position, child.Position);
            ExpectMap(items, 1, 0, firstRevision);

            storage.ReleaseAll();
            var published = await publishing.WaitAsync(Bound);
            await child.WaitForPositionAsync(published.Position, Bound);
            Assert.True(published.Changed);
            Assert.Equal(published.Position, node.Position);
            Assert.True(items.TryGet(1L, out _, out var publishedRevision));
            Assert.NotEqual(firstRevision, publishedRevision);
            ExpectMap(items, 1, Commands, publishedRevision);
            ExpectMap(child.Set("Holder"), 1, Commands, publishedRevision);
            AssertDropped(publicationOwners);
            OwnershipAudit.Expect(node);

            storage.Hold();
            var predecessor = items.DeleteAsync(99L).AsTask();
            await storage.NextAsync();
            using var cancellation = new CancellationTokenSource();
            var withdrawing = Update(items, 2).CommitAsync(cancellation.Token).AsTask();
            var queuedOwners = await OwnersAsync(queued: true);
            Assert.Equal(commandBytes + dictionaryBytes, queuedOwners.Sum(ManagedObjectSize));
            _ = ExpectCapacity(node, allocator, out _, queuedOwners);
            Assert.Equal(published.Position, node.Position);
            ExpectMap(items, 2, 0, unchanged);

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawing.WaitAsync(Bound));
            Assert.False(predecessor.IsCompleted);
            Assert.Equal(published.Position, node.Position);
            ExpectMap(items, 2, 0, unchanged);
            AssertDropped(queuedOwners);

            storage.ReleaseAll();
            var preceding = await predecessor.WaitAsync(Bound);
            await child.WaitForPositionAsync(preceding.Position, Bound);
            var beforeRefusal = ExpectCapacity(node, allocator);
            // Leave room for the independently sized dictionary and its buckets, but less
            // than the real command array required by the next preparation.
            var holdLength = checked((int)(Limit - beforeRefusal.ChargedBytes - commandBytes / 2));
            using (node.HoldBudgetBacking(holdLength))
            {
                var bounded = ExpectCapacity(node, allocator);
                Assert.InRange(Limit - bounded.ChargedBytes, dictionaryBytes, commandBytes - 1);
                await RefusedAsync(Update(items, 2).CommitAsync().AsTask(), LatticeLimitEnum.Memory);
                var refused = ExpectCapacity(node, allocator);
                Assert.Equal(bounded.ChargedBytes, refused.ChargedBytes);
                Assert.Equal(bounded.NativeBytes, refused.NativeBytes);
                Assert.Equal(preceding.Position, node.Position);
                Assert.Equal(preceding.Position, child.Position);
                ExpectMap(items, 2, 0, unchanged);
            }

            OwnershipAudit.Expect(node);
        }
        finally
        {
            storage.ReleaseAll();
        }

        Task<object[]> OwnersAsync(bool queued) =>
            MaintenanceStep.RunAfterAdmissionAsync<object[]>(node, parts =>
            {
                var prepared = Backing<CommitGroup>(parts.Coordinator, "_group").Writes.Single();
                if (queued)
                {
                    var queue = Backing<object>(parts.Coordinator, "_prepared");
                    var write = Assert.IsType<PendingWrite>(queue.GetType().GetProperty("First")!.GetValue(queue));
                    prepared = Assert.IsType<PreparedWrite>(write.Prepared);
                }
                var updates = Assert.IsType<PreparedUpdates>(prepared.Updates);
                var effects = Backing<Dictionary<uint, SparseChange>>(updates, "_effects");
                var saved = effects[0].Commands;
                Assert.Equal(Commands, saved.Length);
                return [effects, .. DictionaryArrays(effects), saved];
            });

        void AssertDropped(object[] objects)
        {
            var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);
            _ = node.InspectBudget((_, backing) => owned.Add(backing));
            Assert.All(objects, backing => Assert.DoesNotContain(backing, owned));
            _ = ExpectCapacity(node, allocator);
        }
    }

    private static LatticeRecord Empty(ILatticeSet items, long id) => new(items.EntityType)
    {
        ["Id"] = id,
        ["Lines"] = new Dictionary<string, string>(),
    };

    private static LatticeUpdate Update(ILatticeSet items, long id, string value = "value")
    {
        var update = items.CreateUpdate(id);
        for (var key = 0; key < Commands; key++)
        {
            update.MapSet("Lines", key.ToString(CultureInfo.InvariantCulture), value);
        }

        return update;
    }

    private static void ExpectMap(ILatticeSet items, long key, int count, LatticeRevision? expected, string value = "value")
    {
        Assert.True(items.TryGet(key, out var record, out var revision));
        Assert.Equal(expected, revision);
        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(record["Lines"]);
        Assert.Equal(count, map.Count);
        if (count > 0)
        {
            Assert.Equal(value, map["0"]);
            Assert.Equal(value, map[(count - 1).ToString(CultureInfo.InvariantCulture)]);
        }
    }
}
