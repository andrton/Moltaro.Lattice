using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Diagnostics;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// What a test says when a write it submitted on a child was committed by the master and the child did not
// apply it within its SynchronizationTimeout: the message of the engine names the position, not why the
// child stayed behind. The forwarding tests that put a large write through a child await it here, and such
// a failure is rethrown with the position the child has applied and the state of the system in its message
// (Diagnostics.SystemState), the original as its inner exception. For diagnosis only: nothing is retried,
// and the code and the committed position stay what the engine reported. How many bytes of a transaction
// a child has received no hook of the engine exposes, so the capture does not say it.
internal static class ApplicationWait
{
    public static async Task<LatticeWriteResult> WithCaptureAsync(Task<LatticeWriteResult> write, ILatticeDatabase child)
    {
        try
        {
            return await write;
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.SynchronizationTimeout)
        {
            throw new LatticeException(exception.Error, $"{exception.Message} {Describe(child)}", exception)
            {
                CommittedPosition = exception.CommittedPosition,
            };
        }
    }

    // The position the child has applied, and what the system had left. The child may have closed or
    // faulted since, so a position it cannot report is written as such in its place.
    private static string Describe(ILatticeDatabase child)
    {
        string position;

        try
        {
            position = child.Position.ToString();
        }
        catch (Exception exception) when (exception is LatticeException or ObjectDisposedException)
        {
            position = $"not readable ({exception.Message})";
        }

        return $"The child has applied position {position}. {SystemState.Describe()}";
    }
}
