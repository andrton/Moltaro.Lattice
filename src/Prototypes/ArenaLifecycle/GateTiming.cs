namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal struct GateTiming
{
    public string Kind { get; set; }
    public int Identity { get; set; }
    public long Start { get; set; }
    public long Acquired { get; set; }
    public long Released { get; set; }
    public bool Contended { get; set; }
    public bool Skipped { get; set; }
}
