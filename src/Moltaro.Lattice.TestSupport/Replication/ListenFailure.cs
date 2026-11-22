using System.Net.Sockets;
using Moltaro.Lattice.TestSupport.Diagnostics;

namespace Moltaro.Lattice.TestSupport.Replication;

// What a test says when the system refuses the socket of a master, a child or a relay it opens. A system
// short of memory, buffers or handles refuses a socket with a message such as "the system lacked
// sufficient buffer space or a queue was full", which alone does not say what was short. The helpers that
// open a listening master, a child or a relay rethrow such a failure with the error of the socket and the
// state of the system in its message (Describe, Diagnostics.SystemState) and the original as its inner
// exception, so that the output kept of a failed run explains it. For diagnosis only: nothing is retried, and the code
// of the failure stays what the engine reported.
internal static class ListenFailure
{
    // Awaits the open of a master that listens, or of a child that connects to its master. An open that
    // fails because the system refused its socket, a LatticeException whose inner exception is a
    // SocketException, is rethrown as a LatticeException with the same code, the message followed by the
    // error of the socket and the state of the system, and the original as its inner exception. Every other
    // outcome passes unchanged.
    public static async Task ExplainAsync(Task open)
    {
        try
        {
            await open;
        }
        catch (LatticeException exception) when (exception.InnerException is SocketException refused)
        {
            throw new LatticeException(exception.Error, $"{exception.Message} {Describe(refused)}", exception);
        }
    }

    // The error the system reported for the socket, and what the system had left.
    public static string Describe(SocketException refused) =>
        $"The socket error is {refused.SocketErrorCode} (native {refused.NativeErrorCode}). {SystemState.Describe()}";
}
