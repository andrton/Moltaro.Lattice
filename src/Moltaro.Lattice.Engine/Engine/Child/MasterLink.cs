using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Child.Loading;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Child;

// The connection of a child to its master, from the first attempt to reach it to the end of the load and
// on, while the child follows its master (docs/design/milestone-1-engine-contract.md, "Replication
// transport and initial loading"). The child owns it, and keeps it open after the load: the stream of
// transactions arrives on it, and the child sends back what it applied (Following.Follower).
//
// Reaching the master (ConnectAsync). While the master cannot be reached the child keeps trying, with
// the back-off of the milestone contract — 100 ms, doubling, at most 5 s between two attempts —, until
// OpenTimeout has passed since the first attempt: a child may be started before its master. A connection
// the master refused or ended before it replied counts as not reached. Once connected, the child sends
// its Hello and waits for the reply for what is left of OpenTimeout: the child has OpenTimeout to reach
// its master and be accepted by it. A Refuse, a reply that is not valid, no reply in that time, and a
// master the child cannot load from (Replication.HelloCheck) end the open at once.
//
// The load (ReceiveSchemaAsync, ReceiveRootsAsync). Every receive waits for the next frame at most
// OpenTimeout: a load that keeps arriving is not ended by the timeout, however long it takes. The
// schema, and then the roots, each placed as soon as it is whole (RootPlacement); then LoadEnd.
//
// The waits, and how each ends: a connect and the reply to the Hello, at the end of the time left to
// reach the master and be accepted by it; a pause between two attempts (Replication.RetryPause), at its
// length or the end of that time, whichever comes first; a receive of the load, after OpenTimeout; a
// send, when the master has taken the bytes — the Hello and an Applied are small and the master reads
// them. The token of the open ends each of them, and the close of the child is in that token. The waits
// of the stream after the load are the follower's.
internal sealed class MasterLink : IDisposable
{
    private readonly FrameConnection _connection;

    private readonly TimeSpan _openTimeout;
    private readonly MemoryBudget _budget;

    private MasterLink(FrameConnection connection, HelloBody master, TimeSpan openTimeout, MemoryBudget budget)
    {
        _connection = connection;
        Master = master;
        _openTimeout = openTimeout;
        _budget = budget;
    }

    // The Hello of the master: its database, its history and the fingerprint of its schema.
    public HelloBody Master { get; }

    // Reaches the master at the address and is accepted by it (see the class). Unavailable when it
    // could not be reached within OpenTimeout, when it refused the child, or when the child cannot load
    // from it, also when it accepted the connection and replied nothing within the time left;
    // ValidationFailed for a reply that is not valid; OperationCanceledException for the token.
    public static async Task<MasterLink> ConnectAsync(Uri masterUri, TimeSpan openTimeout, HelloBody hello, MemoryBudget budget,
        CancellationToken cancellationToken)
    {
        var endPoint = EndPointOf(masterUri);
        var started = Stopwatch.GetTimestamp();
        var pause = default(RetryPause);

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

                connection = new FrameConnection(socket, budget);
                connection.AddHello(FrameKindEnum.Hello, hello);
                await connection.SendAsync(cancellationToken).ConfigureAwait(false);

                var accepting = openTimeout - Stopwatch.GetElapsedTime(started);

                if (accepting <= TimeSpan.Zero)
                {
                    throw NotAccepted(masterUri, openTimeout, null);
                }

                var reply = await connection.ReceiveAsync(accepting, cancellationToken).ConfigureAwait(false);
                var master = MasterOf(reply, hello, masterUri);

                return new MasterLink(connection, master, openTimeout, budget);
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
            catch (TimeoutException exception)
            {
                Close(connection, socket);

                throw NotAccepted(masterUri, openTimeout, exception);
            }
            catch
            {
                Close(connection, socket);
                throw;
            }

            var left = openTimeout - Stopwatch.GetElapsedTime(started);

            if (left > TimeSpan.Zero)
            {
                var next = pause.Next();

                await Task.Delay(next < left ? next : left, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Receives the first part of the load: the schema of the master and the count of roots of each of
    // its entity types (Replication.Load.SchemaTransfer). Schema bytes beyond the memory the child may
    // take for its database cannot be held: Unavailable with the reason.
    public async Task<(LatticeSchema Schema, long[] Counts)> ReceiveSchemaAsync(long memoryLimit, CancellationToken cancellationToken)
    {
        using var bytes = new TransferBuffer(_budget);

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

            if (frame.EndsABody)
            {
                return SchemaTransfer.Read(bytes.Sequence, _budget);
            }
        }
    }

    // Receives the roots and places each as soon as it is whole, until LoadEnd, ends the load with it
    // (RootPlacement.End) and returns it. A root announced beyond the memory the child may take for its
    // database cannot be held: Unavailable with the reason.
    public async Task<LoadEndBody> ReceiveRootsAsync(RootPlacement placement, long memoryLimit, CancellationToken cancellationToken)
    {
        using var assembler = new RootAssembler(memoryLimit, _budget);
        var completed = new List<ReadOnlySequence<byte>>();

        try
        {
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
                        var end = LoadEndBody.Read(frame.Payload.Span);

                        placement.End(end);

                        return end;

                    default:
                        throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The master sent a {frame.Kind} frame in the middle of the roots of its load.");
                }
            }
        }
        finally
        {
            var backing = BudgetCollections.Items(completed);
            completed.Clear();
            completed.Capacity = 0;
            _budget.Release(backing);
        }
    }

    // After the load, for the child that follows its master (Following.Follower): the next frame of the
    // stream, waited for at most the timeout, or until the master sends one, ends the connection, or the
    // token ends the wait when it is null; as FrameConnection.ReceiveAsync.
    public ValueTask<ReceivedFrame> ReceiveAsync(TimeSpan? timeout, CancellationToken cancellationToken) =>
        _connection.ReceiveAsync(timeout, cancellationToken);

    // Whether the next frame of the stream has arrived whole already (FrameConnection.HoldsFrame).
    public bool HoldsFrame => _connection.HoldsFrame;

    // After the load, for the one task that sends on the connection (Forwarding.ChildSender): adds an
    // Applied, which tells the master that the child has applied everything it received up to the
    // sequence, to the frames waiting to be sent.
    public void AddApplied(ulong sequence)
    {
        Span<byte> payload = stackalloc byte[Protocol.AppliedPayloadSize];

        TransactionFormat.WriteApplied(payload, sequence);
        _connection.Add(FrameKindEnum.Applied, 0, payload);
    }

    // The same, for one frame of a forwarded write, the last one marked.
    public void AddRequest(ulong correlationId, bool last, ReadOnlySpan<byte> payload) =>
        _connection.Add(FrameKindEnum.WriteRequest, last ? Protocol.EndsABody : 0, correlationId, payload);

    // Sends the frames added. The send waits until the master has taken the bytes, which it does as long
    // as it serves the child; the token ends the wait, and so does the end of the connection.
    public ValueTask SendAsync(CancellationToken cancellationToken) => _connection.SendAsync(cancellationToken);

    // Ends the connection in order. The master sees the child go, and holds nothing for it afterwards.
    public void Dispose() => _connection.Dispose();

    public void ReleaseBacking() => _connection.ReleaseBacking();

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

    private static LatticeException NotAccepted(Uri masterUri, TimeSpan openTimeout, Exception? innerException) =>
        new(LatticeErrorEnum.Unavailable, $"The master at {masterUri} did not accept this child within the open timeout of {openTimeout}: it replied nothing to the Hello.", innerException);

    private static void Close(FrameConnection? connection, Socket socket)
    {
        if (connection is not null)
        {
            connection.Reset();
            connection.ReleaseBacking();
        }
        else
        {
            socket.Dispose();
        }
    }
}
