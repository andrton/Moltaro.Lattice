namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The stop of a run that its threads look at.
internal sealed class RunControl
{
    private int _stopped;

    public bool Stopped => Volatile.Read(ref _stopped) != 0;

    public void Stop() => Volatile.Write(ref _stopped, 1);
}
