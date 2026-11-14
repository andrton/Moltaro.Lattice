using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed record BatchMeasurement(int Size, LatencyResult EndToEnd, LatencyResult Preparation,
    LatencyResult Storage, LatencyResult PublicationWait, LatencyResult PublicationHold,
    long MaximumPublicationAllocatedBytes, long MaximumPreparedNativeBytes, long MaximumNativeCapacityIncrease);
