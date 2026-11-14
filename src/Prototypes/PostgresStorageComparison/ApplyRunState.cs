namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class ApplyRunState
{
    private ApplyProgress _published = new(0, 0);
    private ApplyProgress _applied = new(0, 0);
    private int _writerFinished;

    public ApplyProgress Published => Volatile.Read(ref _published);
    public ApplyProgress Applied => Volatile.Read(ref _applied);
    public bool WriterFinished => Volatile.Read(ref _writerFinished) != 0;
    public void Publish(ApplyProgress progress) => Volatile.Write(ref _published, progress);
    public void AppliedThrough(ApplyProgress progress) => Volatile.Write(ref _applied, progress);
    public void FinishWriter() => Volatile.Write(ref _writerFinished, 1);
}
