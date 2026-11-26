using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Maintenance;

// A report of one turn for tests and measurements. The elapsed ticks include admission and
// the protected work. The observer sees the value once the turn leaves the close barrier.
internal readonly record struct MaintenanceTurnSample(
    bool Admitted,
    long ElapsedTicks,
    int CandidatesExamined,
    int SelectionWork,
    int ReclamationWork,
    ArenaAddress Source,
    ArenaAddress Destination,
    int SlotBytes,
    int SegmentsReturned,
    long ReturnedNativeBytes,
    int RelocationBytes,
    BlockKindEnum SourceKind = default,
    ushort SourceFlags = 0,
    DescriptorKindEnum SourceDescriptorKind = default);
