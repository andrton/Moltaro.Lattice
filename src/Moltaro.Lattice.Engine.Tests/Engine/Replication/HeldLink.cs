using System.Net.Sockets;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// A link between one child and its master (LoopbackLink) that holds back what the master sends, as a
// slow network would, for the tests that need a child in the middle of its load without a switch inside
// the engine. The child is given Address as its MasterUri; what it sends passes at once. The link knows
// nothing of the frames. It works in one of two ways:
// - Started, it passes the first Passed bytes from the master and holds the rest until Release. Passed
//   is more than a handshake and a schema of these tests take, a few KiB, and much less than the
//   entities the tests store (Pieces.InsertBeyondALinkAsync), so a child that has received Passed bytes
//   has had its handshake and cannot have had the whole database: it is Loading for as long as the
//   link holds.
// - Paced, it passes the bytes from the master in portions, one portion and then a pause, so that the
//   load keeps arriving and takes as long as the test wants. A portion of at least two frames of the
//   largest body (64 KiB, Protocol.MaxDataBody) completes a frame in every pause, so no wait of the child
//   for the next frame is longer than two pauses.
// Cut ends both connections with a reset, as the network does when the process of a peer is gone.
internal sealed class HeldLink : IAsyncDisposable
{
    // What a started link passes from the master to the child before it holds the rest.
    public const int Passed = 256 * 1024;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // The bytes from the master after which the link holds or pauses; and the pause of a paced link,
    // null for a link that holds until Release.
    private readonly int _portion;

    private readonly TimeSpan? _pause;

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly LoopbackLink _link;

    private HeldLink(Uri masterUri, int portion, TimeSpan? pause)
    {
        _portion = portion;
        _pause = pause;
        _link = new LoopbackLink(masterUri, CopyToChildAsync);
    }

    // The address the child is given.
    public Uri Address => _link.Address;

    // A link to the master at the address that holds after Passed bytes until Release.
    public static HeldLink Start(Uri masterUri) => new(masterUri, Passed, pause: null);

    // A link to the master at the address that passes a portion of the bytes and then pauses, again and
    // again.
    public static HeldLink Paced(Uri masterUri, int portion, TimeSpan pause) => new(masterUri, portion, pause);

    // Completes once a started link has passed Passed bytes from the master to the child and holds the
    // rest; fails when that has not happened within the bound.
    public Task HeldAsync() => _held.Task.WaitAsync(Bound);

    // Lets the rest through, from now on without holding.
    public void Release() => _released.TrySetResult();

    // Ends both connections with a reset.
    public void Cut() => _link.Cut();

    public ValueTask DisposeAsync() => _link.DisposeAsync();

    // Copies what the master sends to the child, and holds or pauses after every portion.
    private async Task CopyToChildAsync(Socket master, Socket child, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long beforeTheHold = _portion;

        while (true)
        {
            var read = await master.ReceiveAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, beforeTheHold)), SocketFlags.None, cancellationToken);

            if (read == 0)
            {
                return;
            }

            await LoopbackLink.SendAsync(child, buffer.AsMemory(0, read), cancellationToken);
            beforeTheHold -= read;

            if (beforeTheHold > 0)
            {
                continue;
            }

            if (_pause is { } pause)
            {
                await Task.Delay(pause, cancellationToken);
                beforeTheHold = _portion;
            }
            else
            {
                _held.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
                beforeTheHold = long.MaxValue;
            }
        }
    }
}
