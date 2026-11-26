using System.Net.Sockets;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// A link between one child and its master (LoopbackLink) that reads what the master sends frame by frame
// (WireFrame) and passes each frame through the change a test gives: as it is, changed, dropped or more
// than once. What the child sends passes unchanged. The link keeps every frame each side sent, in the
// order it arrived, so that a test can read the handshake and the frames of a load, and every frame it
// passed to the child while the child was connected, so that a test can state that a frame it changed
// reached the child before the child ended its connection. A frame is recorded before it is sent, since a
// child that refuses it may end its connection before the send returns. The frames of the master are those
// a node of the engine wrote, so their lengths are trusted; a change runs on the thread of the copy, and an
// assertion that fails in it ends the link and fails DisposeAsync. A test learns that the connection of the
// child has ended — the child closed or reset it, or the link was cut — from ChildEndedAsync. An end in
// order of the master's side reaches the child in order as well, after everything the relay passed, and not
// as the reset with which the link ends a connection otherwise.
internal sealed class FrameRelay : IAsyncDisposable
{
    // Guards what the copies keep, which a test reads from its own thread.
    private readonly Lock _lock = new();

    private readonly TaskCompletionSource _childEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<WireFrame> _fromMaster = [];

    private readonly List<byte> _fromChild = [];

    private readonly List<WireFrame> _toChild = [];

    private readonly Func<WireFrame, IEnumerable<WireFrame>> _change;

    private readonly LoopbackLink _link;

    private FrameRelay(Uri masterUri, Func<WireFrame, IEnumerable<WireFrame>> change)
    {
        _change = change;
        _link = new LoopbackLink(masterUri, CopyToChildAsync, CopyToMasterAsync);
    }

    // The address the child is given.
    public Uri Address => _link.Address;

    // Every frame the master sent so far, before any change.
    public IReadOnlyList<WireFrame> FromMaster
    {
        get
        {
            lock (_lock)
            {
                return [.. _fromMaster];
            }
        }
    }

    // Every frame the relay passed to the child so far, in the order it sent them, each recorded before its
    // send began while the copy from the child had not ended: the child was connected when the bytes went to
    // it.
    public IReadOnlyList<WireFrame> ToChild
    {
        get
        {
            lock (_lock)
            {
                return [.. _toChild];
            }
        }
    }

    // Every whole frame the child sent so far.
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

    // A relay that passes every frame as it is.
    public static FrameRelay Start(Uri masterUri) => new(masterUri, frame => [frame]);

    // A relay that passes, in the place of each frame of the master, the frames the change returns.
    public static FrameRelay Start(Uri masterUri, Func<WireFrame, IEnumerable<WireFrame>> change) => new(masterUri, change);

    // Completes once the copy from the child has ended: the child ended its connection, or the link was
    // cut or ended; fails when that has not happened within the bound.
    public Task ChildEndedAsync() => BoundedWait.ForAsync(_childEnded.Task, "The end of the connection of the child");

    public void Cut() => _link.Cut();

    public ValueTask DisposeAsync() => _link.DisposeAsync();

    private async Task CopyToChildAsync(Socket master, Socket child, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var pending = new List<byte>();

        while (true)
        {
            var read = await master.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);

            if (read == 0)
            {
                // The master ended its side in order: so does the relay toward the child, which reads
                // what was passed and then the end, as from its master, and the copy waits until the
                // child ends its side and the link ends.
                child.Shutdown(SocketShutdown.Send);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            pending.AddRange(buffer.AsSpan(0, read));

            while (WireFrame.TryTake([.. pending], out var frame))
            {
                pending.RemoveRange(0, frame.Bytes.Length);

                lock (_lock)
                {
                    _fromMaster.Add(frame);
                }

                foreach (var passed in _change(frame))
                {
                    lock (_lock)
                    {
                        if (!_childEnded.Task.IsCompleted)
                        {
                            _toChild.Add(passed);
                        }
                    }

                    await LoopbackLink.SendAsync(child, passed.Bytes, cancellationToken);
                }
            }
        }
    }

    private async Task CopyToMasterAsync(Socket child, Socket master, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        try
        {
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

                await LoopbackLink.SendAsync(master, buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            _childEnded.TrySetResult();
        }
    }
}
