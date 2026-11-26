using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Maintenance;

// One node's bounded maintenance. Each admitted turn drains the eligible FIFO
// prefix before seeking an earlier slot for at most one move. The writes lock
// protects the cursors. A turn keeps only numeric coordinates and the expected
// owner across unlock, and the mover verifies them against the current blocks
// (docs/design/arena-protocol.md, "Reclamation and relocation").
internal sealed class AutomaticMaintenance(Action? beforeTextMigrationPageAllocation = null)
{
    public const int SelectionWorkLimit = 128;

    public const int ReclamationWorkLimit = 64;

    private long _extent;
    private long _region;
    private int _slot = -1;

    private ArenaAddress _source;
    private RootHandle _owner;
    private BlockKindEnum _kind;
    private int _sizeClass;
    private long _destination;

    public void Run(DatabaseParts parts) => _ = Step(parts);

    public MaintenanceTurnSample Step(DatabaseParts parts)
    {
        var arena = parts.Arena;
        var segments = arena.Counters.Segments;
        // Text work gets one chance even when every raw retirement unit
        // is needed. An idle text table leaves the entire allowance to the FIFO.
        var textWork = arena.Texts.MaintainBackground(1, beforeTextMigrationPageAllocation, out var deferred);
        var reclaimed = parts.Retired.DrainStep(ReclamationWorkLimit - textWork);
        var remaining = ReclamationWorkLimit - textWork - reclaimed;
        if (!deferred && remaining != 0)
        {
            textWork += arena.Texts.MaintainBackground(remaining, beforeTextMigrationPageAllocation, out _);
        }
        arena.ReleaseCollectionTail();
        var work = 0;
        var candidates = 0;
        ArenaAddress source = default;
        ArenaAddress destination = default;
        var slotBytes = 0;
        (BlockKindEnum Kind, ushort Flags, DescriptorKindEnum DescriptorKind) sourceShape = default;

        if (!parts.Coordinator.HoldsPreparedMemory())
        {
            if (!_source.IsNull)
            {
                // A resumed candidate is examined again before using its cursor. The
                // owner edge is still checked by the mover when it finds a vacancy.
                work++;
                candidates++;
                if (!arena.IsRelocationSource(_source, _sizeClass, _owner, _kind))
                {
                    _source = default;
                }
            }

            // Start another sweep only on a new turn. Capture its extent once so
            // continuous growth cannot make the sweep unending.
            if (_source.IsNull && _region == _extent)
            {
                _region = 0;
                _extent = arena.RelocationRegionExtent;
            }

            while (work < SelectionWorkLimit)
            {
                if (!_source.IsNull)
                {
                    if (TryNextVacancy(parts, ref work, out source, out destination, out slotBytes, out sourceShape))
                    {
                        break;
                    }
                }
                else if (_region < _extent)
                {
                    SeekSource(arena, ref work, ref candidates);
                }
                else
                {
                    break;
                }
            }
        }

        // Neither a directed move nor reclamation obtains a segment. A decrease
        // here therefore counts actual native returns, separately from the reserve.
        var returned = segments - arena.Counters.Segments;
        return new MaintenanceTurnSample(true, 0, candidates, work, reclaimed + textWork, source, destination, slotBytes,
            returned, (long)returned * arena.SegmentSize, parts.Retired.RelocationBytes, sourceShape.Kind,
            sourceShape.Flags, sourceShape.DescriptorKind, textWork);
    }

    private void SeekSource(Arena arena, ref int work, ref int candidates)
    {
        work++;
        if (_slot < 0)
        {
            if (arena.RelocationSlotsAt(_region) == 0)
            {
                _region++;
            }
            else
            {
                _slot = 0;
            }

            return;
        }

        var address = arena.RelocationSlotAt(_region, _slot, out var slots, out var sizeClass);
        if (++_slot >= slots)
        {
            _slot = -1;
            _region++;
        }

        if (address.IsNull)
        {
            return;
        }

        candidates++;
        ref readonly var header = ref arena.Header(address);
        if (header.Kind is BlockKindEnum.Root or BlockKindEnum.Value or BlockKindEnum.Chunk
            or BlockKindEnum.ChunkDirectory or BlockKindEnum.Lookup or BlockKindEnum.SharedText
            or BlockKindEnum.SharedTextDirectory or BlockKindEnum.SharedTextChunk)
        {
            _source = address;
            _owner = header.Root;
            _kind = header.Kind;
            _sizeClass = sizeClass;
            _destination = -1;
        }
    }

    private bool TryNextVacancy(DatabaseParts parts, ref int work, out ArenaAddress source,
        out ArenaAddress destination, out int slotBytes,
        out (BlockKindEnum Kind, ushort Flags, DescriptorKindEnum DescriptorKind) sourceShape)
    {
        source = default;
        destination = default;
        slotBytes = 0;
        sourceShape = default;
        var arena = parts.Arena;
        var bytes = SizeClasses.SizeOf(_sizeClass);
        var earlier = arena.EarlierRelocationExtent(_source);
        if (bytes > Arena.MaxBlockSize - parts.Retired.RelocationBytes || earlier == 0 || _destination == earlier)
        {
            _source = default;
            return false;
        }

        work++;
        var region = _destination < 0
            ? arena.RelocationHead(_sizeClass, _source.Segment)
            : arena.RelocationVacancyAt(_destination, _sizeClass);
        _destination++;
        if (region == 0)
        {
            return false;
        }

        var old = _source;
        _source = default;
        // A shared locator has no descriptor. Only its chunks have an
        // incoming Block descriptor, which the text mover validates.
        var incoming = _kind == BlockKindEnum.SharedTextChunk ? DescriptorKindEnum.Block : default;
        var moved = _kind switch
        {
            BlockKindEnum.Root => RootRelocation.TryMove(parts, _owner, old, region, out destination),
            BlockKindEnum.SharedText or BlockKindEnum.SharedTextDirectory or BlockKindEnum.SharedTextChunk =>
                TextRelocation.TryMove(parts, _owner, old, region, out destination),
            _ => CompoundRelocation.TryMove(parts, _owner, old, region, out destination, out incoming),
        };
        if (moved)
        {
            source = old;
            slotBytes = bytes;
            // The source was just queued for retirement under this same Writes
            // lock. Capture its flags before entering the observer.
            sourceShape = (_kind, arena.Header(old).Flags, incoming);
        }

        // A vacancy admits one owner walk in a turn, even if validation or
        // the retirement queue refuses the move. Resume with the next source.
        return true;
    }
}
