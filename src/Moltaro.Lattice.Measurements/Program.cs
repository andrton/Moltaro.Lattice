using System.Globalization;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Measurements.Manifests;
using Moltaro.Lattice.Measurements.Scenarios;

// The measurement program of the engine (docs/work/epic-1-core.md, "Test lanes"). It is started by
// hand, from src, with the name of a scenario and the name of the manifest the run is made under:
//
//   dotnet run -c Release --project Moltaro.Lattice.Measurements -- root-bytes development
//
// It writes the report of the run, the manifest included, to the console and to a file under
// .local/measurements in the current directory: from src, that is src/.local, which Git ignores,
// because raw output is never committed. Exit code 0 is a completed run, 2 a command line it does
// not understand.
const string Usage = "Usage: Moltaro.Lattice.Measurements <scenario> <manifest>";

if (args.Length != 2)
{
    return Refuse(Usage);
}

var (scenario, manifestName) = (args[0], args[1]);
var manifest = RunManifest.Find(manifestName);

if (manifest is null)
{
    return Refuse($"There is no manifest {manifestName}. Manifests: {string.Join(", ", RunManifest.Available())}.");
}

IReadOnlyList<string>? results = scenario switch
{
    RootBytesScenario.Name => RootBytesScenario.Run(),
    ReadByKeyScenario.Name => ReadByKeyScenario.Run(),
    ReadByTextKeyScenario.Name => ReadByTextKeyScenario.Run(),
    _ => null,
};

if (results is null)
{
    return Refuse($"There is no scenario {scenario}. Scenarios: {RootBytesScenario.Name}, {ReadByKeyScenario.Name}, {ReadByTextKeyScenario.Name}.");
}

var finished = DateTimeOffset.UtcNow;
List<string> report =
[
    $"Scenario {scenario}, manifest {manifest.Name}, finished {finished.ToString("u", CultureInfo.InvariantCulture)}",
    $"Observed: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical processors",
    "",
    .. results,
    "",
    $"Manifest {manifest.Name}:",
    manifest.Text.TrimEnd(),
];

var folder = Path.Combine(".local", "measurements");
var file = Path.Combine(folder, $"{scenario}-{finished.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");

Directory.CreateDirectory(folder);
File.WriteAllLines(file, report);

foreach (var line in report)
{
    Console.WriteLine(line);
}

Console.WriteLine();
Console.WriteLine($"Written to {file}.");

return 0;

static int Refuse(string message)
{
    Console.Error.WriteLine(message);

    return 2;
}
