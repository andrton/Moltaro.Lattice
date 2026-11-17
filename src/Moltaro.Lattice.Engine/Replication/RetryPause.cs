namespace Moltaro.Lattice.Replication;

// The pauses between two attempts of the transport that failed (milestone contract, "Initial bounded
// settings", the row "Reconnect / applier failure pauses"): 100 ms, doubling after each, at most 5 s.
// They are the pace of the attempts, not a timeout or a limit: what bounds how long a node goes on
// trying is said where it tries — the OpenTimeout of a child that tries to reach its master
// (Engine.Loading.MasterLink), the close of a master whose accepting failed (Engine.Children.ChildListener).
// They are constants in the code until S34, which builds reconnection and makes them options (exception
// X6).
//
// A value, one for each place that retries, used by the one task that retries there.
internal struct RetryPause
{
    private static readonly TimeSpan First = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(5);

    // The pause the next failure waits; default before the first.
    private TimeSpan _next;

    // The pause after a failure, and the next one doubled up to the longest.
    public TimeSpan Next()
    {
        var pause = _next == default ? First : _next;

        _next = pause * 2 < Longest ? pause * 2 : Longest;

        return pause;
    }

    // After an attempt that succeeded: the next failure waits the first pause again.
    public void Reset() => _next = default;
}
