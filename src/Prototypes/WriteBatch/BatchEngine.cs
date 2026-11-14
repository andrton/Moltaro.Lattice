using System.Diagnostics;
using System.Security.Cryptography;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed class BatchEngine : IDisposable
{
    #region Ownership and coordinator

    public const int MaximumBatch = 32;
    private readonly ArenaModel _arena = new(true);
    private readonly ReaderWriterLockSlim _publication = new(LockRecursionPolicy.NoRecursion);
    // This is writer admission/order ownership, not an arena protection lock.
    // The bounded experiment serializes preparation too; parallel preparation is
    // outside this prototype. No publication/root/index gate spans storage I/O.
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Dictionary<Guid, RetainedReceipt> _memoryReceipts = new(512);
    private readonly PgBatchStore? _store;
    private readonly long _preparationLimit;
    private bool _faulted;
    private long _position;

    public BatchEngine(BatchMutation[] initial, PgBatchStore? store = null, long preparationLimit = 64 * 1024)
    {
        _store = store;
        _preparationLimit = preparationLimit;
        try
        {
            var recovery = store is null ? RecoveryState.Initial(initial) : store.ReadRecovery();
            foreach (var root in recovery.Roots)
            {
                using var prepared = _arena.Prepare(root.Mutation.Value);
                _arena.Publish(prepared, root.Revision);
            }

            _arena.RestoreRevisionFloor(recovery.Revision);
            _position = recovery.Position;

            if (store is not null)
            {
                foreach (var batch in recovery.Tail)
                {
                    Check.That(batch.Receipt.Position == _position + 1, "Journal position gap.");
                    var changes = BatchCodec.Decode(batch.Payload);
                    Validate(changes);
                    Check.That(changes.Length == batch.Receipt.MutationCount, "Receipt count mismatch.");
                    var plans = Prepare(changes, null);
                    try
                    {
                        Publish(changes, plans, batch.Receipt, null);
                    }
                    finally
                    {
                        DisposePlans(plans);
                    }
                }
            }
        }
        catch
        {
            _arena.Dispose();
            _publication.Dispose();
            _writer.Dispose();
            throw;
        }
    }

    public long Position => Interlocked.Read(ref _position);
    public int WaitingReaders => _publication.WaitingReadCount;
    public int WaitingPublishers => _publication.WaitingWriteCount;
    public bool Faulted => Volatile.Read(ref _faulted);

    private void CheckReady() => Check.That(!Faulted, "Lattice instance requires reopen.");

    #endregion

    #region Protected entry points

    public ReadSummary? Read(EntityKey identity)
    {
        _publication.EnterReadLock();
        try
        {
            CheckReady();
            return _arena.Read(identity.Slot);
        }
        finally
        {
            _publication.ExitReadLock();
        }
    }

    public int Query(string text, Span<RootClaim> scratch, Span<ReadSummary> results)
    {
        _publication.EnterReadLock();
        try
        {
            CheckReady();
            return _arena.Query(text, text, long.MinValue, long.MaxValue, scratch, results);
        }
        finally
        {
            _publication.ExitReadLock();
        }
    }

    public bool CompactStep()
    {
        if (!_publication.TryEnterReadLock(0))
        {
            return false;
        }

        try
        {
            CheckReady();
            return _arena.CompactStep();
        }
        finally
        {
            _publication.ExitReadLock();
        }
    }

    public NativeMemorySnapshot Snapshot()
    {
        _publication.EnterReadLock();
        try
        {
            CheckReady();
            return _arena.Allocator.Snapshot();
        }
        finally
        {
            _publication.ExitReadLock();
        }
    }

    public void Audit(ValueSpec?[] expected)
    {
        _publication.EnterReadLock();
        try
        {
            CheckReady();
            IndexChecks.VerifyQueries(_arena, expected);
        }
        finally
        {
            _publication.ExitReadLock();
        }
    }

    public void HeldReadForTest(Action held)
    {
        _publication.EnterReadLock();
        try
        {
            CheckReady();
            held();
        }
        finally
        {
            _publication.ExitReadLock();
        }
    }

    #endregion

    #region Bounded preparation and atomic publication

    public BatchObservation Write(Guid operationId, BatchMutation[] changes,
        Action<BatchPhaseEnum, int>? hook = null)
    {
        Check.That(operationId != Guid.Empty, "Operation identity is required.");
        Validate(changes);
        Check.That(_writer.Wait(TimeSpan.FromSeconds(3)), "Writer admission timed out.");
        PreparedSave?[]? plans = null;
        var committed = false;
        try
        {
            CheckReady();
            var payload = BatchCodec.Encode(changes);
            var fingerprint = SHA256.HashData(payload);
            var previous = _store?.Find(operationId) ?? _memoryReceipts.GetValueOrDefault(operationId);
            if (previous is not null)
            {
                Check.That(previous.Fingerprint.AsSpan().SequenceEqual(fingerprint), "Operation token reused with different input.");
                Check.That(previous.Receipt.Position <= _position, "Committed operation requires reopen before retry.");
                return new BatchObservation(previous.Receipt, true, 0, 0, 0, 0, 0, 0, 0, 0);
            }

            Check.That(_store is not null || _memoryReceipts.Count < 512, "MemoryOnly receipt fixture limit.");
            var before = _arena.Allocator.Snapshot();
            var started = Stopwatch.GetTimestamp();
            plans = Prepare(changes, hook);
            var prepareTicks = Stopwatch.GetTimestamp() - started;
            var prepared = _arena.Allocator.Snapshot();
            var receipt = new BatchReceipt(_position + 1, changes.Length);
            var stored = new StoredBatch(operationId, receipt, payload);
            var retained = new RetainedReceipt(receipt, fingerprint);
            hook?.Invoke(BatchPhaseEnum.Prepared, changes.Length);
            started = Stopwatch.GetTimestamp();
            if (_store is null)
            {
                hook?.Invoke(BatchPhaseEnum.BeforeCommit, 0);
            }
            else
            {
                _store.Append(stored, hook);
            }

            committed = true;
            var storageTicks = Stopwatch.GetTimestamp() - started;
            hook?.Invoke(BatchPhaseEnum.AfterCommit, 0);
            var publication = Publish(changes, plans, receipt, hook);
            if (_store is null)
            {
                _memoryReceipts.Add(operationId, retained);
            }

            hook?.Invoke(BatchPhaseEnum.AfterPublication, changes.Length);
            return new BatchObservation(receipt, false, prepareTicks, storageTicks,
                publication.Wait, publication.Hold, publication.Allocated,
                prepared.PreparedCapacityBytes, before.AllocatedBytes, prepared.AllocatedBytes);
        }
        catch (CommitFailureException exception) when (!exception.Uncertain)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (committed || exception is CommitFailureException { Uncertain: true })
            {
                // Publish also sets this before opening its barrier on failure.
                Volatile.Write(ref _faulted, true);
            }

            throw;
        }
        finally
        {
            if (plans is not null)
            {
                DisposePlans(plans);
            }

            _writer.Release();
        }
    }

    private void Validate(BatchMutation[] changes)
    {
        Check.That(changes.Length is > 0 and <= MaximumBatch, "Batch member limit.");
        Span<bool> seen = stackalloc bool[ArenaModel.KeyLimit];
        seen.Clear();
        long required = 0;
        foreach (var change in changes)
        {
            Check.That(change.Identity.Type is EntityTypeEnum.User or EntityTypeEnum.Role &&
                change.Identity.Key is >= 0 and < 512, "Fixture identity limit.");
            Check.That(!seen[change.Identity.Slot], "Duplicate type/key rejects the whole batch.");
            seen[change.Identity.Slot] = true;
            Check.That(change.Kind is MutationKindEnum.Upsert or MutationKindEnum.Delete, "Unsupported mutation.");
            if (change.Kind == MutationKindEnum.Upsert)
            {
                var value = change.Value;
                Check.That(value.Key == change.Identity.Slot && value.TextGroup is >= 0 and < TextTable.Count &&
                    value.Length is > 0 and <= RootCodec.MaximumChunks * RootCodec.ChunkBytes, "Invalid replacement.");
                required += RootCodec.Bytes + ((value.Length + 255) / 256) * 256 + 64;
            }
        }

        Check.That(required <= _preparationLimit, "Native preparation budget rejected before commit.");
    }

    private PreparedSave?[] Prepare(BatchMutation[] changes, Action<BatchPhaseEnum, int>? hook)
    {
        var plans = new PreparedSave?[changes.Length];
        try
        {
            // Coordinator ownership prevents mutations while plans retain nodes.
            // Compaction and readers still use the arena's existing short gates.
            var insertions = changes.Count(change => change.Kind == MutationKindEnum.Upsert && _arena.Read(change.Identity.Slot) is null);
            Check.That(_arena.IndexCount + insertions <= _arena.IndexCapacity, "Primary capacity not reserved.");
            for (var index = 0; index < changes.Length; index++)
            {
                if (changes[index].Kind == MutationKindEnum.Upsert)
                {
                    plans[index] = _arena.Prepare(changes[index].Value);
                }

                hook?.Invoke(BatchPhaseEnum.MemberPrepared, index + 1);
            }

            return plans;
        }
        catch
        {
            DisposePlans(plans);
            throw;
        }
    }

    private (long Wait, long Hold, long Allocated) Publish(BatchMutation[] changes, PreparedSave?[] plans,
        BatchReceipt receipt, Action<BatchPhaseEnum, int>? hook)
    {
        var requested = Stopwatch.GetTimestamp();
        _publication.EnterWriteLock();
        var acquired = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CheckReady();
            for (var index = 0; index < changes.Length; index++)
            {
                if (plans[index] is { } save)
                {
                    _arena.Publish(save);
                }
                else
                {
                    _arena.Delete(changes[index].Identity.Slot);
                }

                hook?.Invoke(BatchPhaseEnum.MemberPublished, index + 1);
            }

            Interlocked.Exchange(ref _position, receipt.Position);
            return (acquired - requested, Stopwatch.GetTimestamp() - acquired,
                GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        catch
        {
            Volatile.Write(ref _faulted, true);
            throw;
        }
        finally
        {
            _publication.ExitWriteLock();
        }
    }

    private static void DisposePlans(PreparedSave?[] plans)
    {
        foreach (var plan in plans)
        {
            plan?.Dispose();
        }
    }

    #endregion

    public void Dispose()
    {
        // Harness joins all users before close; reopen creates one replacement arena.
        _arena.Dispose();
        _publication.Dispose();
        _writer.Dispose();
    }
}
