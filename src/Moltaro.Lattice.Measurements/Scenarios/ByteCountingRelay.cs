using System.Net;
using System.Net.Sockets;

namespace Moltaro.Lattice.Measurements.Scenarios;

// A relay on the loopback interface between one child and its master that counts the bytes each way, for
// the scenario child-load: the child is given Address as its master's address, the relay accepts its one
// connection, connects to the master and copies each way until one side ends its connection. It reads
// nothing of what it copies. A relay adds a hop to the path of every byte, so the scenario times loads
// without it and counts bytes with it.
internal sealed class ByteCountingRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    private readonly IPEndPoint _master;

    private readonly CancellationTokenSource _ended = new();

    private readonly Task _run;

    private long _toChild;

    private long _toMaster;

    public ByteCountingRelay(Uri masterUri)
    {
        _master = new IPEndPoint(IPAddress.Parse(masterUri.Host), masterUri.Port);
        _listener.Start(1);
        Address = new Uri($"lattice://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _run = RunAsync();
    }

    // The address the child is given.
    public Uri Address { get; }

    // The bytes copied from the master to the child, and from the child to the master.
    public long ToChild => Interlocked.Read(ref _toChild);

    public long ToMaster => Interlocked.Read(ref _toMaster);

    public async ValueTask DisposeAsync()
    {
        await _ended.CancelAsync();
        _listener.Stop();

        try
        {
            await _run;
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The relay ended because it was disposed, or a peer had ended its connection.
        }

        _ended.Dispose();
    }

    private async Task RunAsync()
    {
        using var child = await _listener.AcceptSocketAsync(_ended.Token);
        using var master = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        _listener.Stop();
        await master.ConnectAsync(_master, _ended.Token);

        var toChild = CopyAsync(master, child, count => Interlocked.Add(ref _toChild, count));
        var toMaster = CopyAsync(child, master, count => Interlocked.Add(ref _toMaster, count));

        // One side ended its connection; the other is ended too.
        await Task.WhenAny(toChild, toMaster);
        child.Close();
        master.Close();

        await Task.WhenAll(toChild, toMaster);
    }

    private async Task CopyAsync(Socket from, Socket to, Action<int> counted)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, _ended.Token);

                if (read == 0)
                {
                    return;
                }

                counted(read);

                var unsent = buffer.AsMemory(0, read);

                while (!unsent.IsEmpty)
                {
                    unsent = unsent[await to.SendAsync(unsent, SocketFlags.None, _ended.Token)..];
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The relay was disposed, or a peer ended its connection.
        }
    }
}
