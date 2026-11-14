using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.ReplicaStreamComparison;

internal static class Program
{
    #region Experiment

    private static async Task Main(string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out var sizeMiB) ||
            sizeMiB is < 4 or > 10240 || sizeMiB % 4 != 0)
        {
            throw new ArgumentException("Usage: ReplicaStreamComparison <sizeMiB: 4..10240, multiple of 4>");
        }

        var preparation = Stopwatch.StartNew();
        var source = MemoryImage.CreateSource(sizeMiB);
        preparation.Stop();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        MemoryImage destination;
        var transfer = new Stopwatch();
        try
        {
            listener.Start(1);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            transfer.Start();
            var send = SendAsync(listener, source, timeout.Token);
            var receive = ReceiveAsync(port, sizeMiB, timeout.Token);
            await Task.WhenAll(send, receive);
            destination = await receive;
            transfer.Stop();
        }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
        }

        var verification = Stopwatch.StartNew();
        destination.Verify();
        verification.Stop();
        var payloadBytes = sizeMiB * 1024L * 1024;

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Scope = "One process, two arenas, one loopback TCP connection; not a database startup",
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PayloadBytes = payloadBytes,
            ResidentArenaPayloadBytes = 2 * payloadBytes,
            MemoryImage.SegmentBytes,
            SourcePreparationSeconds = preparation.Elapsed.TotalSeconds,
            TransferAndDestinationAllocationSeconds = transfer.Elapsed.TotalSeconds,
            VerificationSeconds = verification.Elapsed.TotalSeconds,
            TransferAndVerificationSeconds = transfer.Elapsed.TotalSeconds + verification.Elapsed.TotalSeconds,
            PayloadGiBPerSecond = payloadBytes / 1073741824.0 / transfer.Elapsed.TotalSeconds,
            CorrectnessChecksPassed = true
        }, new JsonSerializerOptions { WriteIndented = true }));

        GC.KeepAlive(source);
        GC.KeepAlive(destination);
    }

    #endregion

    #region Transport

    private static async Task SendAsync(TcpListener listener, MemoryImage source, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();
        foreach (var segment in source.Segments)
        {
            await stream.WriteAsync(segment, cancellationToken);
        }
    }

    private static async Task<MemoryImage> ReceiveAsync(int port, int sizeMiB, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        using var stream = client.GetStream();
        var destination = new MemoryImage(sizeMiB);
        for (var index = 0; index < destination.Segments.Length; index++)
        {
            var segment = new byte[MemoryImage.SegmentBytes];
            await stream.ReadExactlyAsync(segment, cancellationToken);
            destination.Segments[index] = segment;
        }
        return destination;
    }

    #endregion
}
