using System.Diagnostics;

namespace Moltaro.Lattice.Measurements.Scenarios;

// This measurement program in a second process, for a scenario that puts one side in a process of its own
// (RemoteChild, RemoteForwarder): started the way the first process was, with the arguments given and its
// input and output redirected. Started by its own executable, the process is that executable; started as
// "dotnet <assembly>", the process is the host dotnet, which takes the assembly as its first argument. The
// arguments the program reads are the same either way.
internal static class ProgramProcess
{
    public static ProcessStartInfo StartInfo(params string[] arguments)
    {
        var host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        if (Path.GetFileNameWithoutExtension(host) == "dotnet")
        {
            start.ArgumentList.Add(typeof(ProgramProcess).Assembly.Location);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
