namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class ArenaModel : IDisposable
{
    #region Bounded catalog and gates

    public const int KeyLimit = 1024;
    private readonly object _catalog = new();
    private readonly int[]? _index;
    private readonly ArenaIndexes? _indexes;
    private readonly RootEntry[] _entries = new RootEntry[KeyLimit + 32];
    private readonly Stack<int> _freeEntries = new(KeyLimit + 32);
    private readonly ReaderWriterLockSlim[] _rootGates = Enumerable.Range(0, 64)
        .Select(_ => new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion)).ToArray();
    private readonly ArenaAllocator _allocator = new();
    private readonly TextTable _texts;
    private long _revision;

    public ArenaModel(bool indexed = false, StringComparison comparison = StringComparison.Ordinal, bool edgeText = false)
    {
        _texts = new TextTable(_allocator, comparison, edgeText);
        if (indexed)
        {
            _indexes = new ArenaIndexes(_texts);
        }
        else
        {
            _index = new int[KeyLimit];
            Array.Fill(_index, -1);
        }
        for (var slot = _entries.Length - 1; slot >= 0; slot--)
        {
            _freeEntries.Push(slot);
        }
    }

    public ArenaAllocator Allocator => _allocator;
    public TextTable Texts => _texts;
    public int IndexCount => _indexes?.Count ?? 0;
    public int IndexCapacity => _indexes?.PrimaryCapacity ?? 0;
    private int Lookup(int key) => _indexes is not null ? _indexes.Lookup(key) : _index![key];
    public int WaitingTextWriters => _texts.WaitingWriters;
    private ReaderWriterLockSlim Gate(RootClaim claim) => _rootGates[claim.Entry % _rootGates.Length];
    public int WaitingRootWriters(RootClaim claim) => Gate(claim).WaitingWriteCount;

    public RootClaim? Pin(int key)
    {
        using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
        {
            var slot = Lookup(key);
            if (slot < 0)
            {
                return null;
            }

            _entries[slot].Claims++;
            return new RootClaim(slot, _entries[slot].Generation);
        }
    }

    private RootClaim? PinOwner(BlockOwner owner)
    {
        using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
        {
            ref var entry = ref _entries[owner.Entry];
            if (!entry.Live || entry.Generation != owner.Generation)
            {
                return null;
            }

            entry.Claims++;
            return new RootClaim(owner.Entry, owner.Generation);
        }
    }

    public void Unpin(RootClaim claim)
    {
        using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
        {
            ref var entry = ref _entries[claim.Entry];
            Check.That(entry.Generation == claim.Generation && entry.Claims > 0, "Invalid root claim release.");
            entry.Claims--;
            if (entry.Claims == 0 && !entry.Live)
            {
                _freeEntries.Push(claim.Entry);
            }
        }
    }

    private bool IsLive(RootClaim claim) =>
        _entries[claim.Entry].Live && _entries[claim.Entry].Generation == claim.Generation;

    #endregion

    #region Detached reads and protected calculations

    public ReadSummary? Read(int key)
    {
        TailProbe.Mark("read/pin");
        var claim = Pin(key);
        if (claim is null)
        {
            return null;
        }

        try
        {
            TailProbe.Mark("read/protected-payload");
            return ReadClaim(claim.Value);
        }
        finally
        {
            TailProbe.Mark("read/unpin");
            Unpin(claim.Value);
        }
    }

    public ReadSummary? ReadClaim(RootClaim claim) => ReadCore(claim, false, null, false).Summary;

    public OwnedDocument? Copy(int key)
    {
        var claim = Pin(key);
        if (claim is null)
        {
            return null;
        }

        try
        {
            var result = ReadCore(claim.Value, true, null, false);
            return result.Summary is { } summary
                ? new OwnedDocument(summary, result.Payload!, result.Text!)
                : null;
        }
        finally
        {
            Unpin(claim.Value);
        }
    }

    public void HeldReadForTest(int key, Action held, bool holdText = false)
    {
        var claim = Pin(key) ?? throw new InvalidOperationException("Missing test root.");
        try
        {
            ReadCore(claim, false, held, holdText);
        }
        finally
        {
            Unpin(claim);
        }
    }

    private (ReadSummary? Summary, byte[]? Payload, string? Text) ReadCore(
        RootClaim claim, bool copy, Action? held, bool holdText)
    {
        var gate = Gate(claim);
        GateTrace.EnterRead(gate, "root/read");
        try
        {
            if (!IsLive(claim))
            {
                return default;
            }

            var root = _allocator.Span(_entries[claim.Entry].Address, RootCodec.Bytes);
            var key = RootCodec.Int(root, 0);
            var number = RootCodec.Long(root, 8);
            var length = RootCodec.Int(root, 32);
            var count = RootCodec.Int(root, 36);
            var seed = RootCodec.Int(root, 4);
            Check.That(key == _entries[claim.Entry].Key && RootCodec.Long(root, 16) == ~number,
                "Torn root fields.");
            Check.That(length > 0 && length <= RootCodec.MaximumChunks * RootCodec.ChunkBytes &&
                count == (length + RootCodec.ChunkBytes - 1) / RootCodec.ChunkBytes, "Invalid chunk directory.");

            var payload = copy ? new byte[length] : null;
            ulong checksum = 0;
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                var start = ordinal * RootCodec.ChunkBytes;
                var bytes = _allocator.Span(RootCodec.Chunk(root, ordinal), Math.Min(RootCodec.ChunkBytes, length - start));
                for (var index = 0; index < bytes.Length; index++)
                {
                    Check.That(bytes[index] == RootCodec.Pattern(seed, start + index), "Mixed or corrupt payload.");
                    checksum += bytes[index];
                }

                if (payload is not null)
                {
                    bytes.CopyTo(payload.AsSpan(start));
                }
            }

            if (!holdText)
            {
                held?.Invoke();
            }

            var text = RootCodec.Text(root);
            var copiedText = _texts.Read(text, copy, holdText ? held : null);
            return (new ReadSummary(key, number, RootCodec.Long(root, 24), seed, length,
                text.Entry, checksum), payload, copiedText);
        }
        finally
        {
            GateTrace.ExitRead(gate);
        }
    }

    #endregion

    #region Single ordered writer and private preparation

    public PreparedSave Prepare(ValueSpec value)
    {
        Check.That(value.Length > 0 && value.Length <= RootCodec.ChunkBytes * RootCodec.MaximumChunks,
            "Fixture payload limit.");
        RootClaim claim;
        using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
        {
            var slot = Lookup(value.Key);
            if (slot < 0)
            {
                slot = _freeEntries.Pop();
                ref var entry = ref _entries[slot];
                entry.Key = value.Key;
                entry.Generation = checked(entry.Generation + 1);
            }

            _entries[slot].Claims++;
            claim = new RootClaim(slot, _entries[slot].Generation);
        }

        var prepared = new PreparedSave(this, claim);
        try
        {
            prepared.Text = _texts.Prepare(value.TextGroup);
            prepared.Root = _allocator.Allocate(RootCodec.Bytes, RootCodec.Bytes,
                new BlockOwner(BlockKindEnum.Root, claim.Entry, claim.Generation));
            var count = (value.Length + RootCodec.ChunkBytes - 1) / RootCodec.ChunkBytes;
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                var start = ordinal * RootCodec.ChunkBytes;
                var length = Math.Min(RootCodec.ChunkBytes, value.Length - start);
                var address = _allocator.Allocate(RootCodec.ChunkBytes, length,
                    new BlockOwner(BlockKindEnum.Chunk, claim.Entry, claim.Generation, ordinal));
                prepared.Chunks.Add(address);
                var bytes = _allocator.Span(address, length);
                for (var index = 0; index < length; index++)
                {
                    bytes[index] = RootCodec.Pattern(value.Seed, start + index);
                }
            }

            RootCodec.Initialize(_allocator.Span(prepared.Root.Value, RootCodec.Bytes),
                value, prepared.Text.Value, prepared.Chunks.ToArray());
            if (_indexes is not null)
            {
                var gate = Gate(claim);
                GateTrace.EnterRead(gate, "root/read");
                try
                {
                    using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
                    {
                        CompositeIndexKey? previous = null;
                        if (IsLive(claim))
                        {
                            var old = _allocator.Span(_entries[claim.Entry].Address, RootCodec.Bytes);
                            previous = _indexes.Stored(RootCodec.Text(old), RootCodec.Long(old, 8));
                        }

                        prepared.Index = _indexes.Prepare(claim,
                            _indexes.Stored(prepared.Text.Value, value.Number), previous);
                    }
                }
                finally
                {
                    GateTrace.ExitRead(gate);
                }
            }

            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    public void Abort(PreparedSave prepared)
    {
        if (prepared.Index is { } index)
        {
            _indexes!.Abort(index);
        }

        foreach (var chunk in prepared.Chunks)
        {
            _allocator.Free(chunk);
        }

        if (prepared.Root is { } root)
        {
            _allocator.Free(root);
        }

        if (prepared.Text is { } text)
        {
            _texts.Release(text, true);
        }

        prepared.Completed = true;
        Unpin(prepared.Claim);
    }

    public void Publish(PreparedSave prepared, long? restoredRevision = null)
    {
        Check.That(!prepared.Completed, "Save already completed.");
        var claim = prepared.Claim;
        var gate = Gate(claim);
        GateTrace.EnterWrite(gate, "root/write");
        try
        {
            ref var entry = ref _entries[claim.Entry];
            Check.That(entry.Generation == claim.Generation, "Wrong prepared lifetime.");
            var oldAddress = entry.Live ? entry.Address : (BlockAddress?)null;
            var root = prepared.Root!.Value;
            var revision = restoredRevision ?? (_revision + 1);
            Check.That(revision > 0, "Invalid restored revision.");
            _revision = Math.Max(_revision, revision);
            RootCodec.Long(_allocator.Span(root, RootCodec.Bytes), 24, revision);
            _texts.Publish(prepared.Text!.Value);
            foreach (var chunk in prepared.Chunks)
            {
                _allocator.Publish(chunk);
            }

            _allocator.Publish(root);
            using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
            {
                if (_indexes is not null)
                {
                    _indexes.Publish(entry.Key, prepared.Index!);
                }
                else
                {
                    _index![entry.Key] = claim.Entry;
                }

                entry.Address = root;
                entry.Live = true;
            }

            if (oldAddress is { } previous)
            {
                FreeOwned(previous);
            }

            prepared.Completed = true;
        }
        finally
        {
            GateTrace.ExitWrite(gate);
        }

        Unpin(claim);
    }

    public void Save(ValueSpec value)
    {
        TailProbe.Mark("save/prepare");
        using var prepared = Prepare(value);
        TailProbe.Mark("save/publish");
        Publish(prepared);
        TailProbe.Mark("save/dispose-completed-plan");
    }

    // Recovery only, before the arena becomes accessible. The highest assigned
    // revision may belong to a root that was subsequently deleted from the base.
    public void RestoreRevisionFloor(long revision)
    {
        Check.That(revision >= _revision, "Recovery revision precedes a saved root.");
        _revision = revision;
    }

    public bool UpdateNumber(int key, long number)
    {
        var claim = Pin(key);
        if (claim is null)
        {
            return false;
        }

        var gate = Gate(claim.Value);
        GateTrace.EnterWrite(gate, "root/write");
        try
        {
            if (!IsLive(claim.Value))
            {
                return false;
            }

            var root = _allocator.Span(_entries[claim.Value.Entry].Address, RootCodec.Bytes);
            using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
            {
                _indexes?.ChangeNumber(claim.Value,
                    _indexes.Stored(RootCodec.Text(root), RootCodec.Long(root, 8)), number);
                RootCodec.Long(root, 8, number);
                RootCodec.Long(root, 16, ~number);
                RootCodec.Long(root, 24, ++_revision);
            }
            return true;
        }
        finally
        {
            GateTrace.ExitWrite(gate);
            Unpin(claim.Value);
        }
    }

    public bool Delete(int key)
    {
        var claim = Pin(key);
        if (claim is null)
        {
            return false;
        }

        var gate = Gate(claim.Value);
        GateTrace.EnterWrite(gate, "root/write");
        try
        {
            if (!IsLive(claim.Value))
            {
                return false;
            }

            ref var entry = ref _entries[claim.Value.Entry];
            var address = entry.Address;
            using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
            {
                if (_indexes is not null)
                {
                    var root = _allocator.Span(address, RootCodec.Bytes);
                    _indexes.Remove(key, claim.Value,
                        _indexes.Stored(RootCodec.Text(root), RootCodec.Long(root, 8)));
                }
                else
                {
                    _index![key] = -1;
                }

                entry.Live = false;
            }

            FreeOwned(address);
            return true;
        }
        finally
        {
            GateTrace.ExitWrite(gate);
            Unpin(claim.Value);
        }
    }

    private void FreeOwned(BlockAddress address)
    {
        var root = _allocator.Span(address, RootCodec.Bytes);
        for (var ordinal = 0; ordinal < RootCodec.Int(root, 36); ordinal++)
        {
            _allocator.Free(RootCodec.Chunk(root, ordinal));
        }

        _texts.Release(RootCodec.Text(root), false);
        _allocator.Free(address);
    }

    #endregion

    #region Bounded indexed query and protected candidate handoff

    public int CaptureCandidates(string lowerText, string upperText, long lowerNumber, long upperNumber,
        Span<RootClaim> destination)
    {
        var indexes = _indexes ?? throw new InvalidOperationException("Indexes are disabled in this control fixture.");
        using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
        {
            var count = indexes.Collect(indexes.Probe(lowerText, lowerNumber),
                indexes.Probe(upperText, upperNumber), destination);
            for (var index = 0; index < count; index++)
            {
                var claim = destination[index];
                Check.That(IsLive(claim), "Index retained a dead lifetime.");
                _entries[claim.Entry].Claims++;
            }

            return count;
        }
    }

    public int ReadCandidates(ReadOnlySpan<RootClaim> candidates, string lowerText, string upperText,
        long lowerNumber, long upperNumber, Span<ReadSummary> destination)
    {
        var written = 0;
        try
        {
            foreach (var claim in candidates)
            {
                var result = ReadClaim(claim);
                if (result is not { } value)
                {
                    continue;
                }

                // The detached summary carries a fixture text ID. Its immutable
                // fixture value represents the text validated during the root read.
                var text = _texts.Value(value.TextGroup);
                var low = string.Compare(text, lowerText, _texts.Comparison);
                var high = string.Compare(text, upperText, _texts.Comparison);
                if ((low > 0 || low == 0 && value.Number >= lowerNumber) &&
                    (high < 0 || high == 0 && value.Number <= upperNumber))
                {
                    Check.That(written < destination.Length, "Result buffer exhausted.");
                    destination[written++] = value;
                }
            }
        }
        finally
        {
            foreach (var claim in candidates)
            {
                Unpin(claim);
            }
        }

        return written;
    }

    public int Query(string lowerText, string upperText, long lowerNumber, long upperNumber,
        Span<RootClaim> scratch, Span<ReadSummary> destination)
    {
        var count = CaptureCandidates(lowerText, upperText, lowerNumber, upperNumber, scratch);
        return ReadCandidates(scratch[..count], lowerText, upperText, lowerNumber, upperNumber, destination);
    }

    public void HeldIndexComparisonForTest(Action held)
    {
        using (GateTrace.EnterMonitor(_catalog, "catalog/index"))
        {
            _texts.HoldComparisonForTest(held);
        }
    }

    #endregion

    #region Compaction with owner revalidation

    public bool CompactStep() => _allocator.TryCandidate(out var candidate) && TryMove(candidate);

    public bool TryMove(AllocationInfo candidate)
    {
        if (candidate.Owner.Kind == BlockKindEnum.Text)
        {
            return _texts.TryMove(candidate);
        }

        var claim = PinOwner(candidate.Owner);
        if (claim is null)
        {
            return false;
        }

        var gate = Gate(claim.Value);
        if (!GateTrace.TryWrite(gate, "root/try-write"))
        {
            Unpin(claim.Value);
            return false;
        }

        try
        {
            if (!IsLive(claim.Value))
            {
                return false;
            }

            ref var entry = ref _entries[claim.Value.Entry];
            var root = _allocator.Span(entry.Address, RootCodec.Bytes);
            BlockAddress? current = entry.Address;
            if (candidate.Owner.Kind == BlockKindEnum.Chunk)
            {
                current = candidate.Owner.Ordinal < RootCodec.Int(root, 36)
                    ? RootCodec.Chunk(root, candidate.Owner.Ordinal)
                    : null;
            }
            if (current != candidate.Address || !_allocator.TryReserveMove(candidate, out var destination))
            {
                return false;
            }

            if (candidate.Owner.Kind == BlockKindEnum.Root)
            {
                entry.Address = destination;
            }
            else
            {
                RootCodec.Chunk(root, candidate.Owner.Ordinal, destination);
            }

            _allocator.FinishMove(candidate, destination);
            return true;
        }
        finally
        {
            GateTrace.ExitWrite(gate);
            Unpin(claim.Value);
        }
    }

    public int DrainCompaction()
    {
        var moved = 0;
        for (var step = 0; step < 8192; step++)
        {
            if (!_allocator.TryCandidate(out var candidate))
            {
                return moved;
            }

            Check.That(TryMove(candidate), "Quiescent compaction candidate did not move.");
            moved++;
        }

        throw new InvalidOperationException("Bounded compaction did not converge.");
    }

    #endregion

    #region Quiescent independent ownership audit

    public void Audit(ValueSpec?[] expected)
    {
        var owners = new Dictionary<BlockAddress, BlockOwner>();
        var textReferences = new int[TextTable.Count];
        for (var key = 0; key < expected.Length; key++)
        {
            var observed = Read(key);
            if (expected[key] is not { } value)
            {
                Check.That(observed is null, "Unexpected live key.");
                continue;
            }

            Check.That(observed is { } result && result.Key == value.Key && result.Number == value.Number &&
                result.Seed == value.Seed && result.Length == value.Length && result.TextGroup == value.TextGroup &&
                result.Revision > 0, "Logical oracle mismatch.");
            var slot = Lookup(key);
            ref var entry = ref _entries[slot];
            var root = _allocator.Span(entry.Address, RootCodec.Bytes);
            owners.Add(entry.Address, new BlockOwner(BlockKindEnum.Root, slot, entry.Generation));
            for (var ordinal = 0; ordinal < RootCodec.Int(root, 36); ordinal++)
            {
                owners.Add(RootCodec.Chunk(root, ordinal),
                    new BlockOwner(BlockKindEnum.Chunk, slot, entry.Generation, ordinal));
            }

            textReferences[RootCodec.Text(root).Entry]++;
        }

        int[]? indexReferences = null;
        if (_indexes is not null)
        {
            indexReferences = new int[TextTable.Count];
            _indexes.Audit(expected, _entries, indexReferences);
        }

        _texts.Audit(textReferences, owners, indexReferences);
        var allocations = _allocator.Allocations();
        Check.That(allocations.Length == owners.Count, "Unreachable native allocation.");
        foreach (var allocation in allocations)
        {
            Check.That(allocation.State == BlockStateEnum.Live &&
                owners.TryGetValue(allocation.Address, out var owner) && owner == allocation.Owner,
                "Allocation/descriptor owner mismatch.");
        }

        Check.That(_entries.All(entry => entry.Claims == 0), "Leaked root claim.");
        var live = expected.Count(value => value is not null);
        Check.That(_freeEntries.Count + live == _entries.Length, "Control entries did not reclaim.");
        Check.That(_allocator.Snapshot().PreparedBlocks == 0, "Preparation did not release.");
    }

    public void Dispose()
    {
        _texts.Dispose();
        foreach (var gate in _rootGates)
        {
            gate.Dispose();
        }

        _allocator.Dispose();
    }

    #endregion
}
