using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Loading;

// The connection of a child to its master, from the first attempt to reach it to the end of the load
// (brief of Epic 1, sprint S10; the planner's blocks of 15 and 17 November 2026 and his decision after the
// study, point 4). The child owns it, and keeps it open after the load, so that the master knows the
// child is connected; until S11 nothing travels on it after the load.
//
// Reaching the master (ConnectAsync). While the master cannot be reached the child keeps trying, with
// the back-off of the milestone contract — 100 ms, doubling, at most 5 s between two attempts —, until
// OpenTimeout has passed since the first attempt: a child may be started before its master. A connection
// the master refused or ended before it replied counts as not reached. Once connected, the child sends
// its Hello and waits OpenTimeout for the reply. A Refuse, a reply that is not valid, and a master the
// child cannot load from (Replication.HelloCheck) end the open at once.
//
// The load (ReceiveSchemaAsync, ReceiveRootsAsync). Every receive waits for the next frame at most
// OpenTimeout: a load that keeps arriving is not ended by the timeout, however long it takes. The
// schema, and then the roots, each placed as soon as it is whole (RootPlacement); then LoadEnd.
//
// The waits, and how each ends: a connect, at the end of the time left to reach the master; a pause
// between two attempts, at its length or the end of that time, whichever comes first; a receive, after
// OpenTimeout; a send, when the master has taken the bytes — the Hello is small and the master reads
// it. The token of the open ends each of them, and the close of the child is in that token.
internal sealed class MasterLink : IDisposable
{
    // The pauses between two attempts to reach the master (milestone contract, "Initial bounded
    // settings", the row "Reconnect / applier failure pauses"): the pace of the attempts, not a timeout
    // or a limit; OpenTimeout bounds how long the child tries.
    private static readonly TimeSpan FirstPause = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(5);

    private readonly FrameConnection _connection;

    private readonly TimeSpan _openTimeout;

    private MasterLink(FrameConnection connection, HelloBody master, TimeSpan openTimeout)
    {
        _connection = connection;
        Master = master;
        _openTimeout = openTimeout;
    }

    // The Hello of the master: its database, its history and the fingerprint of its schema.
    public HelloBody Master { get; }

    // Reaches the master at the address and is accepted by it (see the class). Unavailable when it
    // could not be reached within OpenTimeout, when it refused the child, or when the child cannot load
    // from it; ValidationFailed for a reply that is not valid; TimeoutException when the master accepted
    // the connection and replied nothing within OpenTimeout; OperationCanceledException for the token.
    public static async Task<MasterLink> ConnectAsync(Uri masterUri, TimeSpan openTimeout, HelloBody hello, CancellationToken cancellationToken)
    {
        var endPoint = EndPointOf(masterUri);
        var started = Stopwatch.GetTimestamp();
        var pause = FirstPause;

        while (true)
        {
            var remaining = openTimeout - Stopwatch.GetElapsedTime(started);

            if (remaining <= TimeSpan.Zero)
            {
                throw new LatticeException(LatticeErrorEnum.Unavailable, $"The master at {masterUri} could not be reached within the open timeout of {openTimeout}.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            FrameConnection? connection = null;

            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(remaining);
                    await socket.ConnectAsync(endPoint, deadline.Token).ConfigureAwait(false);
                }

                connection = new FrameConnection(socket);
                connection.AddHello(FrameKindEnum.Hello, hello);
                await connection.SendAsync(cancellationToken).ConfigureAwait(false);

                var reply = await connection.ReceiveAsync(openTimeout, cancellationToken).ConfigureAwait(false);
                var master = MasterOf(reply, hello, masterUri);

                return new MasterLink(connection, master, openTimeout);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The time left to reach the master passed during the connect: the next round says so.
                Close(connection, socket);

                continue;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                // Not reached: refused, reset, or ended before the reply. Another attempt, after a pause.
                Close(connection, socket);
            }
            catch
            {
                Close(connection, socket);
                throw;
            }

            var left = openTimeout - Stopwatch.GetElapsedTime(started);

            if (left > TimeSpan.Zero)
            {
                await Task.Delay(pause < left ? pause : left, cancellationToken).ConfigureAwait(false);
            }

            pause = pause * 2 < LongestPause ? pause * 2 : LongestPause;
        }
    }

    // Receives the first part of the load: the schema of the master and the count of roots of each of
    // its entity types (Replication.Load.SchemaTransfer). Schema bytes beyond the memory the child may
    // take for its database cannot be held: Unavailable with the reason.
    public async Task<(LatticeSchema Schema, long[] Counts)> ReceiveSchemaAsync(long memoryLimit, CancellationToken cancellationToken)
    {
        var bytes = new TransferBuffer();

        while (true)
        {
            var frame = await _connection.ReceiveAsync(_openTimeout, cancellationToken).ConfigureAwait(false);

            if (frame.Kind != FrameKindEnum.Schema)
            {
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The master sent a {frame.Kind} frame where the schema comes.");
            }

            if (bytes.Length + frame.Payload.Length > memoryLimit)
            {
                throw new LatticeException(LatticeErrorEnum.Unavailable, $"The schema of the master takes more than the memory limit of this child, {memoryLimit} bytes: the child cannot hold the database of its master.");
            }

            bytes.Write(frame.Payload.Span);

            if (frame.EndsARoot)
            {
                return SchemaTransfer.Read(bytes.Sequence);
            }
        }
    }

    // Receives the roots and places each as soon as it is whole, until LoadEnd, and ends the load with it
    // (RootPlacement.End). A root announced beyond the memory the child may take for its database cannot
    // be held: Unavailable with the reason.
    public async Task ReceiveRootsAsync(RootPlacement placement, long memoryLimit, CancellationToken cancellationToken)
    {
        var assembler = new RootAssembler(memoryLimit);
        var completed = new List<ReadOnlySequence<byte>>();

        while (true)
        {
            var frame = await _connection.ReceiveAsync(_openTimeout, cancellationToken).ConfigureAwait(false);

            switch (frame.Kind)
            {
                case FrameKindEnum.Blocks:
                    assembler.Accept(frame, completed);

                    foreach (var root in completed)
                    {
                        placement.Place(root);
                    }

                    completed.Clear();
                    break;

                case FrameKindEnum.LoadEnd when !assembler.InProgress:
                    placement.End(LoadEndBody.Read(frame.Payload.Span));

                    return;

                default:
                    throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The master sent a {frame.Kind} frame in the middle of the roots of its load.");
            }
        }
    }

    // Ends the connection in order. The master sees the child go, and publishes writes again once no
    // child is registered on it.
    public void Dispose() => _connection.Dispose();

    // The address of the master: an IP address, or a name that the system resolves when it connects.
    private static EndPoint EndPointOf(Uri masterUri) =>
        IPAddress.TryParse(masterUri.DnsSafeHost, out var address)
            ? new IPEndPoint(address, masterUri.Port)
            : new DnsEndPoint(masterUri.DnsSafeHost, masterUri.Port);

    // The Hello of the master from its reply; the reasons of the class end the open.
    private static HelloBody MasterOf(in ReceivedFrame reply, in HelloBody child, Uri masterUri)
    {
        switch (reply.Kind)
        {
            case FrameKindEnum.Refuse:
                var reason = FrameCodec.ReadRefuseBody(new ReadOnlySequence<byte>(reply.Payload));

                throw new LatticeException(LatticeErrorEnum.Unavailable, $"The master at {masterUri} refused this child, for its {reason}.");

            case FrameKindEnum.HelloReply:
                var master = FrameCodec.ReadHelloBody(new ReadOnlySequence<byte>(reply.Payload));

                if (HelloCheck.RefusalOfMaster(child, master) is { } refusal)
                {
                    throw new LatticeException(LatticeErrorEnum.Unavailable, $"This child cannot load from the peer at {masterUri}: {refusal}");
                }

                return master;

            default:
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The master at {masterUri} replied to the Hello with a {reply.Kind} frame.");
        }
    }

    private static void Close(FrameConnection? connection, Socket socket)
    {
        if (connection is not null)
        {
            connection.Reset();
        }
        else
        {
            socket.Dispose();
        }
    }
}
