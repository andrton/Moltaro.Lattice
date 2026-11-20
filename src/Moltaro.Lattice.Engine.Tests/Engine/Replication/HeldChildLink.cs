using System.Net.Sockets;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// A link between one child and its master (LoopbackLink) that holds back what the child sends, as a
// network that stops taking the bytes of a sender would, for the tests of a write through a child that is
// in the middle of being sent (brief of Epic 1, sprint S12). The child is given Address as its MasterUri;
// what the master sends passes at once. Until Hold the link passes everything the child sends; from then
// on it holds the first bytes it reads from the child, and everything after them, until Release, so that
// a send of the child that the fixed receive buffer of the link (LoopbackLink.ReceiveBuffer) and the
// send buffer of the child cannot take waits. It keeps every byte it read
// from the child, held or passed, so that a test can read the child's frames (WireFrame). Cut ends both
// connections with a reset, as the network does when the process of a peer is gone.
internal sealed class HeldChildLink : IAsyncDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // Guards the bytes the link read from the child, which a test reads from its own thread.
    private readonly Lock _lock = new();

    private readonly List<byte> _fromChild = [];

    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly LoopbackLink _link;

    private volatile bool _holding;

    private HeldChildLink(Uri masterUri)
    {
        _link = new LoopbackLink(masterUri, LoopbackLink.CopyAsync, CopyToMasterAsync);
    }

    // The address the child is given.
    public Uri Address => _link.Address;

    // Every whole frame the child sent so far, those the link holds included.
    public IReadOnlyList<WireFrame> FromChild
    {
        get
        {
            lock (_lock)
            {
                return WireFrame.TakeAll([.. _fromChild]);
            }
        }
    }

    public static HeldChildLink Start(Uri masterUri) => new(masterUri);

    // From now on the link holds what the child sends.
    public void Hold() => _holding = true;

    // Holds what the child sends from the moment the child has acknowledged its readiness position (an
    // Applied, the rule of S11), after which an idle child sends nothing until a write.
    public async Task HoldOnceAcknowledgedAsync()
    {
        await Eventually.HoldsAsync(() => FromChild.Any(frame => frame.Kind == FrameKindEnum.Applied), "The child did not acknowledge its readiness position.");
        Hold();
    }

    // Completes once the link holds bytes it read from the child after Hold; fails when that has not
    // happened within the bound.
    public Task HeldAsync() => _held.Task.WaitAsync(Bound);

    // Passes what the link holds, and from now on everything.
    public void Release() => _released.TrySetResult();

    // Ends both connections with a reset.
    public void Cut() => _link.Cut();

    public ValueTask DisposeAsync() => _link.DisposeAsync();

    private async Task CopyToMasterAsync(Socket child, Socket master, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await child.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

            if (read == 0)
            {
                return;
            }

            lock (_lock)
            {
                _fromChild.AddRange(buffer.AsSpan(0, read));
            }

            if (_holding && !_released.Task.IsCompleted)
            {
                _held.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }

            await LoopbackLink.SendAsync(master, buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
