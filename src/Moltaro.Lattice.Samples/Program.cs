using Moltaro.Lattice.Samples.Basics;
using Moltaro.Lattice.Samples.Hosting;
using Moltaro.Lattice.Samples.Indexes;
using Moltaro.Lattice.Samples.Limits;
using Moltaro.Lattice.Samples.Replication;
using Moltaro.Lattice.Samples.Schema;
using Moltaro.Lattice.Samples.Writes;
using Moltaro.Lattice.Schema;

// Whether the feature a sample shows is implemented. A planned sample shows a feature that is in
// the contract but not implemented yet: it runs to the end once the feature exists, and until then
// the program reports where it stopped.
const bool Implemented = true;
const bool Planned = false;

// The schema of the directory, its users and settings. The first sample compiles it from
// Schema/directory.schema.json, and the samples after it that work on the directory use it.
LatticeSchema schema = null!;

// The samples in the learning order, from simple to complex: the file of each, whether its feature
// is implemented, and how it runs. README.md of this project lists the same samples in the same
// order, and the program checks that before it runs the first.
(string File, bool Implemented, Func<Task> Run)[] samples =
[
    ("Schema/SchemaSample.cs", Implemented, () =>
    {
        schema = SchemaSample.Run();
        return Task.CompletedTask;
    }),
    ("Basics/BasicCrudSample.cs", Implemented, () => BasicCrudSample.RunAsync(schema)),
    ("Basics/DynamicAccessSample.cs", Implemented, () => DynamicAccessSample.RunAsync(schema)),
    ("Basics/KeyTypesSample.cs", Implemented, KeyTypesSample.RunAsync),
    ("Basics/ClosedPropertiesSample.cs", Implemented, ClosedPropertiesSample.RunAsync),
    ("Schema/SchemaDeclarationsSample.cs", Implemented, () =>
    {
        SchemaDeclarationsSample.Run();
        return Task.CompletedTask;
    }),
    ("Schema/DictionaryStringsSample.cs", Implemented, DictionaryStringsSample.RunAsync),
    ("Schema/NestedValuesSample.cs", Planned, NestedValuesSample.RunAsync),
    ("Writes/WriteBatchSample.cs", Planned, () => WriteBatchSample.RunAsync(schema)),
    ("Limits/MemoryLimitSample.cs", Implemented, () => MemoryLimitSample.RunAsync(schema)),
    ("Limits/ReadLimitsSample.cs", Implemented, () => ReadLimitsSample.RunAsync(schema)),
    ("Replication/MasterAndChildSample.cs", Implemented, () => MasterAndChildSample.RunAsync(schema)),
    ("Indexes/IndexSample.cs", Planned, () => IndexSample.RunAsync(schema)),
    ("Indexes/UniqueIndexSample.cs", Planned, UniqueIndexSample.RunAsync),
    ("Hosting/DependencyInjectionSample.cs", Implemented, () => DependencyInjectionSample.RunAsync(schema)),
    ("Hosting/IdentitySample.cs", Planned, IdentitySample.RunAsync),
];

CheckReadme([.. samples.Select(sample => sample.File)]);

for (var index = 0; index < samples.Length; index++)
{
    var (file, implemented, run) = samples[index];
    Console.WriteLine($"Sample {index + 1} of {samples.Length}: {file}");

    if (implemented)
    {
        await run();
    }
    else
    {
        await RunPlannedAsync(run, file);
    }
}

static async Task RunPlannedAsync(Func<Task> sample, string file)
{
    try
    {
        await sample();
    }
    catch (NotSupportedException exception)
    {
        Console.WriteLine($"Stopped: {exception.Message} See {file} for the intended use.");
        Console.WriteLine();
    }
}

// The table of README.md has one row for each sample, which starts with the number of the sample
// and a link to its file. The program refuses to run when those rows and its own list disagree,
// so that neither changes alone.
static void CheckReadme(string[] files)
{
    var expected = files.Select((file, index) => $"| {index + 1} | [{file}]({file}) |");

    var listed = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "README.md"))
        .Where(line => line.StartsWith("| ", StringComparison.Ordinal) && line.Length > 2 && char.IsAsciiDigit(line[2]))
        .Select(line => line[..(line.IndexOf(") |", StringComparison.Ordinal) + 3)]);

    if (!listed.SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            $"README.md must list the samples of Program.cs in the same order, one row each:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}");
    }
}
