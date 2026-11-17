using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// A peer of the tests that acts as the master of one child over loopback TCP and sends it a stream the
// test makes by hand (WireFrame): it accepts one connection, waits for the Hello of the child, sends the
// frames it was given, each with the next stream sequence so that their order is valid, and then reads
// what the child sends until the child ends the connection. The child is given Address as its MasterUri.
// A test makes the stream of frames a real master sent, recorded by a relay (FrameRelay), with the one
// thing it states changed. Every wait is bounded, and DisposeAsync ends everything the peer runs.
internal sealed class ScriptedMaster : IAsyncDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    private readonly IReadOnlyList<WireFrame> _frames;

    private readonly CancellationTokenSource _ended = new();

    private readonly TaskCompletionSource _childEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _run;

    private ScriptedMaster(IReadOnlyList<WireFrame> frames)
    {
        _frames = frames;
        _listener.Start(1);

        Address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _run = RunAsync();
    }

    // The address the child is given.
    public Uri Address { get; }

    // A peer that sends the frames, in their order, to the first child that connects.
    public static ScriptedMaster Start(IReadOnlyList<WireFrame> frames) => new(frames);

    // Completes once the child has ended its connection, closed or reset; fails when that has not
    // happened within the bound.
    public Task ChildEndedAsync() => _childEnded.Task.WaitAsync(Bound);

    // Ends the peer; a second call waits for the same end.
    public async ValueTask DisposeAsync()
    {
        await _ended.CancelAsync();
        _listener.Stop();

        await _run.WaitAsync(Bound);
    }

    private async Task RunAsync()
    {
        try
        {
            using var child = await _listener.AcceptSocketAsync(_ended.Token);

            _listener.Stop();

            var received = new List<byte>();
            var buffer = new byte[64 * 1024];

            // The Hello of the child, whatever it holds: the frames that follow were recorded after the
            // Hello of a child that held nothing, as every child that opens does.
            while (!WireFrame.TryTake([.. received], out _))
            {
                var read = await child.ReceiveAsync(buffer, SocketFlags.None, _ended.Token);

                if (read == 0)
                {
                    return;
                }

                received.AddRange(buffer.AsSpan(0, read));
            }

            for (var index = 0; index < _frames.Count; index++)
            {
                await LoopbackLink.SendAsync(child, _frames[index].WithStreamSequence((ulong)index + 1).Bytes, _ended.Token);
            }

            while (await child.ReceiveAsync(buffer, SocketFlags.None, _ended.Token) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The child reset its connection, or the test ended the peer.
        }
        finally
        {
            _childEnded.TrySetResult();
        }
    }
}
