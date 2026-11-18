using System.Diagnostics;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// A child of the engine in a process of its own, for the scenario following-children: so that what a
// master pays for a child that follows it is measured without the child's work on the processors of the
// master's process. The measurement program starts itself with the mode and the address of the master;
// that process opens a child at the address, writes "ready" on its output once the child is Ready, and
// closes the child and ends when its input ends. The process that started it ends its input to close
// it (Dispose).
internal sealed class RemoteChild : IDisposable
{
    public const string Mode = "remote-child";

    private readonly Process _process;

    private RemoteChild(Process process)
    {
        _process = process;
    }

    // Starts the process and returns once its child is Ready.
    public static RemoteChild Start(Uri masterUri)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, [Mode, masterUri.ToString()])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        var process = Process.Start(start) ?? throw new InvalidOperationException("The process of a remote child did not start.");

        if (process.StandardOutput.ReadLine() != "ready")
        {
            process.Kill();
            throw new InvalidOperationException("The remote child did not become Ready.");
        }

        return new RemoteChild(process);
    }

    // In the process of the child: serves until the input ends; 0 when it did.
    public static int Serve(Uri masterUri)
    {
        using var child = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = masterUri,
        });

        child.OpenAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("ready");
        Console.Out.Flush();

        while (Console.In.ReadLine() is not null)
        {
        }

        return 0;
    }

    public void Dispose()
    {
        _process.StandardInput.Close();
        _process.WaitForExit();
        _process.Dispose();
    }
}
