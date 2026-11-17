using System.Net.Sockets;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// A link between one child and its master (LoopbackLink) that reads what the master sends frame by frame
// (WireFrame) and passes each frame through the change a test gives: as it is, changed, dropped or more
// than once. What the child sends passes unchanged. The link keeps every frame each side sent, in the
// order it arrived, so that a test can read the handshake and the frames of a load. The frames of the
// master are those a node of the engine wrote, so their lengths are trusted; a change runs on the thread
// of the copy, and an assertion that fails in it ends the link and fails DisposeAsync.
internal sealed class FrameRelay : IAsyncDisposable
{
    // Guards what the copies keep, which a test reads from its own thread.
    private readonly Lock _lock = new();

    private readonly List<WireFrame> _fromMaster = [];

    private readonly List<byte> _fromChild = [];

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
                return;
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
                    await LoopbackLink.SendAsync(child, passed.Bytes, cancellationToken);
                }
            }
        }
    }

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

            await LoopbackLink.SendAsync(master, buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
