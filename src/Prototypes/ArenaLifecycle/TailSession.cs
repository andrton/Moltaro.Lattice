namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class TailSession
{
    public TailProbe Reader { get; } = new("reader");
    public TailProbe Writer { get; } = new("writer");
    public TailProbe Compactor { get; } = new("compactor");

    public TailThreadReport[] Report() => [Reader.Report(), Writer.Report(), Compactor.Report()];
}
