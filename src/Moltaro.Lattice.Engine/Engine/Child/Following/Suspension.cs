namespace Moltaro.Lattice.Engine.Child.Following;

// One hold on the application of a child, from Follower.Suspend to its Dispose, which resumes it once no
// other hold is left. Disposing it twice, or after the close of the child, does nothing.
internal sealed class Suspension : IDisposable
{
    private Follower? _follower;

    public Suspension(Follower follower)
    {
        _follower = follower;
    }

    public void Dispose() => Interlocked.Exchange(ref _follower, null)?.Resume();
}
