using System.Globalization;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Measurements.Manifests;
using Moltaro.Lattice.Measurements.Scenarios;
using Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

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

// A child in a process of its own, which the scenario following-children starts (RemoteChild).
if (args is [RemoteChild.Mode, var masterUri])
{
    return RemoteChild.Serve(new Uri(masterUri));
}

// A child in a process of its own that forwards writes, which the scenario forwarding-costs starts
// (RemoteForwarder).
if (args is [RemoteForwarder.Mode, var forwardTo, var writes])
{
    return RemoteForwarder.Serve(new Uri(forwardTo), int.Parse(writes, CultureInfo.InvariantCulture));
}

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
    ReadByKeyThreadsScenario.Name => ReadByKeyThreadsScenario.Run(),
    PrimaryIndexScenario.Name => PrimaryIndexScenario.Run(),
    EnumerateScenario.Name => EnumerateScenario.Run(),
    WriteCallersScenario.Name => WriteCallersScenario.Run(),
    LargeWritesScenario.Name => LargeWritesScenario.Run(),
    ChildLoadScenario.Name => ChildLoadScenario.Run(),
    ChildUnderWritesScenario.Name => ChildUnderWritesScenario.Run(),
    FollowingChildrenScenario.Name => FollowingChildrenScenario.Run(),
    LargeWriteChildScenario.Name => LargeWriteChildScenario.Run(),
    ReadersBesideChildScenario.Name => ReadersBesideChildScenario.Run(),
    ClockWaitersScenario.Name => ClockWaitersScenario.Run(),
    ForwardingScenario.Name => ForwardingScenario.Run(),
    ForwardingCostsScenario.Name => ForwardingCostsScenario.Run(),
    DictionaryReadsScenario.Name => DictionaryReadsScenario.Run(),
    DictionaryWritesScenario.Name => DictionaryWritesScenario.Run(),
    DictionaryMemoryScenario.Name => DictionaryMemoryScenario.Run(),
    DictionaryChildScenario.Name => DictionaryChildScenario.Run(),
    IndexSearchScenario.Name => IndexSearchScenario.Run(),
    NestedIndexReadScenario.Name => NestedIndexReadScenario.Run(associative: false),
    NestedIndexReadScenario.CollectionName => NestedIndexReadScenario.Run(associative: true),
    MapLookupScenario.Name => MapLookupScenario.Run(),
    IndexWritesScenario.Name => IndexWritesScenario.Run(),
    IndexReadByKeyScenario.Name => IndexReadByKeyScenario.Run(),
    IndexBuildScenario.Name => IndexBuildScenario.Run(),
    IndexLongTextScenario.Name => IndexLongTextScenario.Run(),
    UniqueWritesScenario.Name => UniqueWritesScenario.Run(),
    BatchWritesScenario.Name => BatchWritesScenario.Run(),
    ReadsBesideBatchesScenario.Name => ReadsBesideBatchesScenario.Run(),
    FullFormUsersScenario.Name => FullFormUsersScenario.Run(),
    ChurnScenario.Name => await ChurnScenario.RunAsync(maintenance: true),
    ChurnScenario.OffName => await ChurnScenario.RunAsync(maintenance: false),
    _ => null,
};

if (results is null)
{
    return Refuse($"There is no scenario {scenario}. Scenarios: {RootBytesScenario.Name}, {ReadByKeyScenario.Name}, {ReadByTextKeyScenario.Name}, {ReadByKeyThreadsScenario.Name}, {PrimaryIndexScenario.Name}, {EnumerateScenario.Name}, {WriteCallersScenario.Name}, {LargeWritesScenario.Name}, {ChildLoadScenario.Name}, {ChildUnderWritesScenario.Name}, {FollowingChildrenScenario.Name}, {LargeWriteChildScenario.Name}, {ReadersBesideChildScenario.Name}, {ClockWaitersScenario.Name}, {ForwardingScenario.Name}, {ForwardingCostsScenario.Name}, {DictionaryReadsScenario.Name}, {DictionaryWritesScenario.Name}, {DictionaryMemoryScenario.Name}, {DictionaryChildScenario.Name}, {IndexSearchScenario.Name}, {IndexWritesScenario.Name}, {IndexReadByKeyScenario.Name}, {IndexBuildScenario.Name}, {IndexLongTextScenario.Name}, {UniqueWritesScenario.Name}, {BatchWritesScenario.Name}, {ReadsBesideBatchesScenario.Name}, {FullFormUsersScenario.Name}, {NestedIndexReadScenario.Name}, {NestedIndexReadScenario.CollectionName}, {MapLookupScenario.Name}, {ChurnScenario.Name}, {ChurnScenario.OffName}.");
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
