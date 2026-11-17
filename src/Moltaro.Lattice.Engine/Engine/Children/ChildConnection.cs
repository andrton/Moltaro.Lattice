using System.Net.Sockets;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;

namespace Moltaro.Lattice.Engine.Children;

// One connection that a master accepted, and the node at its other end, from its Hello to the end of the
// connection (brief of Epic 1, sprint S10; the planner's blocks of 15 and 17 November 2026 and his
// decision after the study). In its order:
// 1. The Hello. The peer has the OpenTimeout of the master to send a valid one (decision after the study,
//    point 5); a peer that sends nothing, a frame that is not valid or not a Hello is disconnected, and
//    nothing is sent to it.
// 2. The check (Replication.HelloCheck). A peer the master does not accept is sent a Refuse with the
//    reason and nothing else, and the connection is closed. Nothing has been registered for it.
// 3. The registration (Capture.Register), under the lock of the writes: from then on the master refuses
//    every write until the connection ends.
// 4. The load: the HelloReply, the Schema frames, the roots in Blocks frames, and LoadEnd with the
//    sequence the registration took and the number of roots sent. The capture copies one root at a time
//    under the protection of reads into the buffer of the connection and the frames are sent after it
//    has left the protection; no lock and no slot of the epoch table is held while bytes are sent.
// 5. Then the connection stays, as long as the child is connected: a loaded child does not follow its
//    master until S11, and sends nothing. The master waits for the end of the connection, and refuses a
//    frame from the child as one that is not valid.
// Every end of the connection ends the registration, whatever the step and whatever the reason: the
// child closed or vanished, a send or a receive failed, a frame was not valid, or the master closes.
//
// The waits, and how each ends. The Hello: within OpenTimeout, or the close of the master. A send: until
// the child has taken the bytes, which a child that does not read never does; it has no deadline until
// S33, and the close of the master ends it (exception X6). The end of a loaded child's connection: when
// the child closes or vanishes, or the master closes.
//
// How the connection ends. In order, when the master refuses the peer or the child ended it: what was
// sent arrives, then the end. By a reset otherwise, the close of the master included, so that the end of
// the connection leaves nothing waiting on the side of the master and its port is free once the close
// has completed.
internal sealed class ChildConnection
{
    private readonly FrameConnection _connection;

    private readonly Capture _capture;

    private readonly TimeSpan _helloTimeout;

    public ChildConnection(Socket socket, Capture capture, TimeSpan helloTimeout)
    {
        _connection = new FrameConnection(socket);
        _capture = capture;
        _helloTimeout = helloTimeout;
    }

    // Ends the connection at once, by a reset: for the close of the master, which then waits for RunAsync
    // to end.
    public void Reset() => _connection.Reset();

    // Serves the peer until the connection ends (see the class). It never fails: a connection has no
    // caller to report to, and its end is the whole of what a failure does to it. The token is the close
    // of the master.
    public async Task RunAsync(CancellationToken closing)
    {
        var registered = false;
        var inOrder = false;

        try
        {
            var hello = await _connection.ReceiveAsync(_helloTimeout, closing).ConfigureAwait(false);

            if (hello.Kind != FrameKindEnum.Hello)
            {
                return;
            }

            var peer = FrameCodec.ReadHelloBody(new(hello.Payload));

            if (HelloCheck.RefusalOfChild(_capture.Hello, peer) is { } reason)
            {
                _connection.AddRefuse(reason);
                await _connection.SendAsync(closing).ConfigureAwait(false);
                inOrder = true;

                return;
            }

            var start = _capture.Register();

            registered = true;
            await LoadAsync(start, closing).ConfigureAwait(false);

            // A loaded child sends nothing until S11: a frame is not valid, and the end of the
            // connection is the child's.
            await _connection.ReceiveAsync(timeout: null, closing).ConfigureAwait(false);
        }
        catch (IOException) when (registered)
        {
            // The child ended the connection: it closed.
            inOrder = true;
        }
        catch (Exception)
        {
            // The connection ends, whatever ended it (see the class): a peer that is gone, a frame that
            // is not valid, the close of the master, or the database refusing to be read after a close
            // or a fault. Nothing else depends on it.
        }
        finally
        {
            if (inOrder)
            {
                _connection.Dispose();
            }
            else
            {
                _connection.Reset();
            }

            if (registered)
            {
                _capture.Unregister();
            }
        }
    }

    // Sends the whole database at the sequence the registration took (step 4 of the class).
    private async Task LoadAsync(CaptureStart start, CancellationToken closing)
    {
        _connection.AddHello(FrameKindEnum.HelloReply, _capture.Hello);
        SchemaTransfer.Add(_connection, _capture.SavedSchema, start.Counts);
        await _connection.SendAsync(closing).ConfigureAwait(false);

        var buffer = new TransferBuffer();
        var framer = new RootFramer(_connection);

        for (var slot = 0; slot < start.SlotExtent; slot++)
        {
            buffer.Clear();

            if (_capture.TryCopy((uint)slot, buffer))
            {
                await framer.AddAsync(buffer, closing).ConfigureAwait(false);
            }
        }

        await framer.FlushAsync(closing).ConfigureAwait(false);

        AddLoadEnd(new LoadEndBody(start.Sequence, framer.Roots));
        await _connection.SendAsync(closing).ConfigureAwait(false);
    }

    private void AddLoadEnd(LoadEndBody end)
    {
        Span<byte> payload = stackalloc byte[Protocol.LoadEndPayloadSize];

        end.WriteTo(payload);
        _connection.Add(FrameKindEnum.LoadEnd, 0, payload);
    }
}
