namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

// Fixed storage allocated before worker threads start; slow operations do not
// allocate a diagnostic object graph while other operations are being measured.
internal sealed class TailCapture
{
    public TailSample Sample { get; set; }
    public GateTiming[] Gates { get; } = new GateTiming[512];
    public StageTiming[] Stages { get; } = new StageTiming[16];
    public int GateCount { get; set; }
    public int StageCount { get; set; }
}
