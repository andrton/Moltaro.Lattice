namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class ArenaAllocator : IDisposable
{
    #region Storage and accounting

    public const int MaximumSegments = 256;
    private readonly object _gate = new();
    private readonly NativeSegment?[] _segments = new NativeSegment[MaximumSegments];
    private readonly AvailableSegments[] _available = [new(), new(), new()];
    private int _reserve = -1;
    private long _allocated;
    private long _peakAllocated;
    private long _prepared;
    private long _peakPrepared;
    private long _released;
    private long _movedBytes;
    private readonly int[] _moves = new int[3];

    private static int Class(int capacity) => capacity switch
    {
        64 => 0,
        128 => 1,
        256 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(capacity))
    };

    #endregion

    #region Allocation and release

    public BlockAddress Allocate(int capacity, int length, BlockOwner owner)
    {
        using (GateTrace.EnterMonitor(_gate, "allocator"))
        {
            var available = _available[Class(capacity)];
            if (available.Count == 0)
            {
                // Empty reserve keeps its class in this experiment. Reassignment
                // to a different class is intentionally not a production policy.
                if (_reserve >= 0 && _segments[_reserve]!.Capacity == capacity)
                {
                    available.Add(_reserve);
                    _reserve = -1;
                }
                else
                {
                    var id = Array.FindIndex(_segments, segment => segment is null);
                    if (id < 0)
                    {
                        throw new OutOfMemoryException("The 2 MiB native fixture budget is exhausted.");
                    }

                    _segments[id] = new NativeSegment(id, capacity);
                    available.Add(id);
                    _allocated += NativeSegment.Bytes;
                    _peakAllocated = Math.Max(_peakAllocated, _allocated);
                }
            }

            return AllocateIn(_segments[available.Min]!, length, owner);
        }
    }

    private BlockAddress AllocateIn(NativeSegment segment, int length, BlockOwner owner)
    {
        Check.That(length > 0 && length <= segment.Capacity, "Allocation length.");
        var slot = segment.FreeSlots.Pop();
        segment.States[slot] = BlockStateEnum.Prepared;
        segment.Owners[slot] = owner;
        segment.Lengths[slot] = length;
        if (segment.FreeSlots.Count == 0)
        {
            _available[Class(segment.Capacity)].Remove(segment.Id);
        }

        _prepared += segment.Capacity;
        _peakPrepared = Math.Max(_peakPrepared, _prepared);
        return new BlockAddress(segment.Id, slot * segment.Capacity);
    }

    public Span<byte> Span(BlockAddress address, int length) =>
        _segments[address.Segment]!.Span(address.Offset, length);

    public void Publish(BlockAddress address)
    {
        using (GateTrace.EnterMonitor(_gate, "allocator"))
        {
            var segment = _segments[address.Segment]!;
            var slot = address.Offset / segment.Capacity;
            Check.That(segment.States[slot] == BlockStateEnum.Prepared, "Publish private allocation once.");
            segment.States[slot] = BlockStateEnum.Live;
            _prepared -= segment.Capacity;
        }
    }

    public void Free(BlockAddress address)
    {
        using (GateTrace.EnterMonitor(_gate, "allocator"))
        {
            var segment = _segments[address.Segment]!;
            var slot = address.Offset / segment.Capacity;
            Check.That(segment.States[slot] != BlockStateEnum.Free, "Double free.");
            if (segment.States[slot] == BlockStateEnum.Prepared)
            {
                _prepared -= segment.Capacity;
            }

            segment.Span(address.Offset, segment.Capacity).Fill(0xdd);
            segment.States[slot] = BlockStateEnum.Free;
            segment.Lengths[slot] = 0;
            segment.FreeSlots.Push(slot);
            _available[Class(segment.Capacity)].Add(segment.Id);
            if (segment.Used == 0)
            {
                _available[Class(segment.Capacity)].Remove(segment.Id);
                if (_reserve < 0)
                {
                    _reserve = segment.Id;
                }
                else
                {
                    segment.Dispose();
                    _segments[segment.Id] = null;
                    _allocated -= NativeSegment.Bytes;
                    _released++;
                }
            }
        }
    }

    #endregion

    #region Bounded movement

    public bool TryCandidate(out AllocationInfo candidate)
    {
        using (GateTrace.EnterMonitor(_gate, "allocator"))
        {
            for (var id = MaximumSegments - 1; id >= 0; id--)
            {
                var segment = _segments[id];
                if (segment is null || segment.Used == 0)
                {
                    continue;
                }

                var available = _available[Class(segment.Capacity)];
                if (available.Count == 0 || available.Min >= id)
                {
                    continue;
                }

                // At most one region's slot table is inspected per candidate.
                for (var examined = 0; examined < segment.States.Length; examined++)
                {
                    var slot = segment.CandidateCursor++ % segment.States.Length;
                    if (segment.States[slot] == BlockStateEnum.Live)
                    {
                        candidate = Info(segment, slot);
                        return true;
                    }
                }

                break;
            }
        }

        candidate = default;
        return false;
    }

    public bool TryReserveMove(AllocationInfo candidate, out BlockAddress destination)
    {
        using (GateTrace.EnterMonitor(_gate, "allocator"))
        {
            var source = _segments[candidate.Address.Segment];
            var available = _available[Class(candidate.Capacity)];
            if (source is null || source.Capacity != candidate.Capacity ||
                available.Count == 0 || available.Min >= source.Id)
            {
                destination = default;
                return false;
            }

            var slot = candidate.Address.Offset / source.Capacity;
            if (Info(source, slot) != candidate)
            {
                destination = default;
                return false;
            }

            destination = AllocateIn(_segments[available.Min]!, candidate.Length, candidate.Owner);
            Span(candidate.Address, candidate.Length).CopyTo(Span(destination, candidate.Length));
            return true;
        }
    }

    public void FinishMove(AllocationInfo source, BlockAddress destination)
    {
        Publish(destination);
        Free(source.Address);
        using (GateTrace.EnterMonitor(_gate, "allocator"))
        {
            _movedBytes += source.Length;
            _moves[(int)source.Owner.Kind]++;
        }
    }

    #endregion

    #region Quiescent audits and snapshots

    private static AllocationInfo Info(NativeSegment segment, int slot) => new(
        new BlockAddress(segment.Id, slot * segment.Capacity), segment.Owners[slot],
        segment.Lengths[slot], segment.Capacity, segment.States[slot]);

    public AllocationInfo[] Allocations()
    {
        using (GateTrace.EnterMonitor(_gate, "allocator"))
        {
            var result = new List<AllocationInfo>();
            foreach (var segment in _segments)
            {
                if (segment is null)
                {
                    continue;
                }

                for (var slot = 0; slot < segment.States.Length; slot++)
                {
                    if (segment.States[slot] != BlockStateEnum.Free)
                    {
                        result.Add(Info(segment, slot));
                    }
                }
            }

            return result.ToArray();
        }
    }

    public NativeMemorySnapshot Snapshot()
    {
        using (GateTrace.EnterMonitor(_gate, "allocator"))
        {
            long useful = 0, slack = 0, reusable = 0;
            var liveBlocks = 0;
            var preparedBlocks = 0;
            foreach (var segment in _segments)
            {
                if (segment is null || segment.Id == _reserve)
                {
                    continue;
                }

                for (var slot = 0; slot < segment.States.Length; slot++)
                {
                    switch (segment.States[slot])
                    {
                        case BlockStateEnum.Live:
                            useful += segment.Lengths[slot];
                            slack += segment.Capacity - segment.Lengths[slot];
                            liveBlocks++;
                            break;
                        case BlockStateEnum.Prepared:
                            preparedBlocks++;
                            break;
                        default:
                            reusable += segment.Capacity;
                            break;
                    }
                }
            }

            var reserveBytes = _reserve < 0 ? 0 : NativeSegment.Bytes;
            Check.That(useful + slack + _prepared + reusable + reserveBytes == _allocated,
                "Native byte categories do not reconcile.");
            return new NativeMemorySnapshot(_allocated, _peakAllocated, useful, slack,
                _prepared, _peakPrepared, reusable, reserveBytes, liveBlocks, preparedBlocks,
                (int)(_allocated / NativeSegment.Bytes), _released, _movedBytes,
                _moves[0], _moves[1], _moves[2]);
        }
    }

    public void Dispose()
    {
        foreach (var segment in _segments)
        {
            segment?.Dispose();
        }
    }

    #endregion
}
