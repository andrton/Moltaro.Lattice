using System.Net.Sockets;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// A link between one child and its master (LoopbackLink) that holds back what the master sends, as a
// slow network would, for the tests that need a child in the middle of its load without a switch inside
// the engine. The child is given Address as its MasterUri; what it sends passes at once. The link knows
// nothing of the frames. It passes the bytes from the master in portions and, after each, either holds
// or pauses:
// - A holding link holds until the test lets the next portion through (PassNext), or everything
//   (Release), so the test decides when each part of the load arrives. Start makes one whose portion is
//   Passed, which its tests only release: Passed is more than a handshake and a schema of these tests
//   take, a few KiB, and much less than the entities the tests store (Pieces.InsertBeyondALinkAsync), so
//   a child that has received Passed bytes has had its handshake and cannot have had the whole database:
//   it is Loading for as long as the link holds. Stepped makes one with the portion a test gives; a
//   portion of at least the largest frame (Protocol.MaxFrame) completes a frame, so no wait of the child
//   for its next frame lasts longer than one hold.
// - A paced link pauses after every portion, so that a load keeps arriving over a longer time than it
//   would take at once, while the master goes on writing. How long a part takes then depends on the
//   machine; a test that needs to know uses a holding link.
// Cut ends both connections with a reset, as the network does when the process of a peer is gone.
internal sealed class HeldLink : IAsyncDisposable
{
    // What a link made by Start passes from the master to the child before it holds the rest.
    public const int Passed = 256 * 1024;

    // The bytes from the master after which the link holds or pauses; and the pause of a paced link,
    // null for a link that holds.
    private readonly int _portion;

    private readonly TimeSpan? _pause;

    // A count each: of the holds of the link, which HeldAsync takes one at a time, and of the portions the
    // test let through. Neither is disposed: a SemaphoreSlim holds no handle of the system unless one is
    // asked for.
    private readonly SemaphoreSlim _holds = new(0);

    private readonly SemaphoreSlim _next = new(0);

    private readonly LoopbackLink _link;

    // Set by Release before it lets the held link go on: from then on the link holds nothing.
    private volatile bool _released;

    private HeldLink(Uri masterUri, int portion, TimeSpan? pause)
    {
        _portion = portion;
        _pause = pause;
        _link = new LoopbackLink(masterUri, CopyToChildAsync);
    }

    // The address the child is given.
    public Uri Address => _link.Address;

    // A holding link to the master at the address whose portion is Passed.
    public static HeldLink Start(Uri masterUri) => new(masterUri, Passed, pause: null);

    // A holding link to the master at the address whose portion is the one given.
    public static HeldLink Stepped(Uri masterUri, int portion) => new(masterUri, portion, pause: null);

    // A link to the master at the address that passes a portion of the bytes and then pauses, again and
    // again.
    public static HeldLink Paced(Uri masterUri, int portion, TimeSpan pause) => new(masterUri, portion, pause);

    // Completes once a holding link has passed a portion from the master to the child and holds the rest;
    // a second call completes at the next hold. Fails when the link has not held within the bound.
    public Task HeldAsync() => _holds.WaitAsync().WaitAsync(Bound);

    // Lets one more portion through, after which a holding link holds again.
    public void PassNext() => _next.Release();

    // Lets the rest through, from now on without holding.
    public void Release()
    {
        _released = true;
        _next.Release();
    }

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
                _holds.Release();
                await _next.WaitAsync(cancellationToken);
                beforeTheHold = _released ? long.MaxValue : _portion;
            }
        }
    }
}
