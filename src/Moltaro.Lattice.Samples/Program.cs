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
// Schema/directory.schema.json and returns it, and the samples after it that work on the directory
// take it.
LatticeSchema schema = null!;

// The samples in the learning order, from simple to complex: the Run method of each, and whether its
// feature is implemented. The method is all an entry names: the file the program prints is that of
// its class. README.md of this project lists the same samples in the same order, and the program
// checks that before it runs the first.
(Delegate Run, bool Implemented)[] samples =
[
    (SchemaSample.Run, Implemented),
    (BasicCrudSample.RunAsync, Implemented),
    (DynamicAccessSample.RunAsync, Implemented),
    (ViewSample.RunAsync, Planned),
    (KeyTypesSample.RunAsync, Implemented),
    (ClosedPropertiesSample.RunAsync, Implemented),
    (SchemaDeclarationsSample.Run, Implemented),
    (DictionaryStringsSample.RunAsync, Implemented),
    (NestedValuesSample.RunAsync, Planned),
    (WriteBatchSample.RunAsync, Implemented),
    (PartialUpdateSample.RunAsync, Implemented),
    (MemoryLimitSample.RunAsync, Implemented),
    (ReadLimitsSample.RunAsync, Implemented),
    (MasterAndChildSample.RunAsync, Implemented),
    (IndexSample.RunAsync, Implemented),
    (UniqueIndexSample.RunAsync, Implemented),
    (DependencyInjectionSample.RunAsync, Implemented),
    (IdentitySample.RunAsync, Planned),
];

string[] files = [.. samples.Select(sample => FileOf(sample.Run.Method.DeclaringType!))];
CheckReadme(files);

for (var index = 0; index < samples.Length; index++)
{
    var (run, implemented) = samples[index];
    Console.WriteLine($"Sample {index + 1} of {samples.Length}: {files[index]}");

    if (implemented)
    {
        await RunAsync(run);
    }
    else
    {
        await RunPlannedAsync(run, files[index]);
    }
}

// Runs a sample by the form of its Run method: the sample that compiles the schema of the directory
// returns it, and the samples that work on the directory take it.
async Task RunAsync(Delegate run)
{
    switch (run)
    {
        case Func<LatticeSchema> compile:
            schema = compile();
            break;

        case Action sample:
            sample();
            break;

        case Func<Task> sample:
            await sample();
            break;

        case Func<LatticeSchema, Task> sample:
            await sample(schema);
            break;

        default:
            throw new InvalidOperationException($"A sample has no Run method of the form {run.GetType()}.");
    }
}

async Task RunPlannedAsync(Delegate run, string file)
{
    try
    {
        await RunAsync(run);
    }
    catch (NotSupportedException exception)
    {
        Console.WriteLine($"Stopped: {exception.Message} See {file} for the intended use.");
        Console.WriteLine();
    }
}

// The file of a sample: a file holds one type and a folder is a namespace, so the class
// Moltaro.Lattice.Samples.Basics.BasicCrudSample is Basics/BasicCrudSample.cs.
static string FileOf(Type sample) =>
    $"{sample.Namespace!["Moltaro.Lattice.Samples.".Length..].Replace('.', '/')}/{sample.Name}.cs";

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
