using System.Diagnostics;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal static class ChurnInputs
{
    public static EngineTestInputs Create(ChurnLedger ledger, bool off, ChurnTurns turns) => new()
    {
        Allocator = ledger,
        IndexSeed = ChurnTrace.Seed,
        DisableBackgroundMaintenance = off,
        ObserveMaintenanceTurn = sample => turns.Offer(new ChurnTurns.Sample(
            Stopwatch.GetTimestamp(), sample.ElapsedTicks, sample.Admitted,
            !sample.Source.IsNull || sample.ReclamationWork != 0, sample.CandidatesExamined, sample.SelectionWork,
            sample.Source.Value, sample.Destination.Value, sample.SlotBytes,
            sample.ReclamationWork, sample.SegmentsReturned, sample.ReturnedNativeBytes, sample.RelocationBytes,
            (ushort)sample.SourceKind, (ushort)sample.SourceFlags, (uint)sample.SourceDescriptorKind, sample.TextMetadataWork)),
    };
}
