using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Moltaro.Lattice.Tests.Views;

// A consumer in an empty directory, outside the repository's props and targets.
// The local feed is filled from the assemblies built for this test run.
internal sealed class SdkViewProject : IDisposable
{
    public const string Version = "0.0.0-s22-sdk-test";

    private readonly string _root = Directory.CreateTempSubdirectory("lattice-view-").FullName;

    public SdkViewProject()
    {
        var target = new DirectoryInfo(AppContext.BaseDirectory);
        Configuration = target.Parent!.Name;
        Target = target.Name;
        Source = FindSource(target);
        Feed = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;

        File.Copy(Path.Combine(Source, "global.json"), Path.Combine(_root, "global.json"));
    }

    public string Source { get; }

    public string Configuration { get; }

    public string Target { get; }

    public string Feed { get; }

    public void Write(string project, string file, string text)
    {
        var location = Directory.CreateDirectory(Path.Combine(_root, project)).FullName;
        File.WriteAllText(Path.Combine(location, file), text);
    }

    public string FileOf(string project, string path) => Path.Combine(_root, project, path);

    public async Task PackBuiltAsync(string name)
    {
        var project = Path.Combine(Source, name, name + ".csproj");
        var result = await DotNetAsync("", "pack", project, "--no-build", "--configuration", Configuration,
            $"-p:PackageVersion={Version}", "--output", Feed, "--nologo");
        Assert.True(result.ExitCode == 0, result.Output);
    }

    public async Task PackModelsAsync(string project)
    {
        var result = await DotNetAsync(project, "pack", "--configuration", Configuration, $"-p:PackageVersion={Version}",
            "--output", Feed, "--nologo");
        Assert.True(result.ExitCode == 0, result.Output);
    }

    public Task<(int ExitCode, string Output)> BuildAsync(string project) =>
        DotNetAsync(project, "build", "--configuration", Configuration, "--nologo", $"-p:RestoreSources={Feed}");

    public Task<(int ExitCode, string Output)> RunAsync(string project) =>
        DotNetAsync(project, "run", "--no-build", "--configuration", Configuration);

    public string[] References(string name)
    {
        using var stream = File.OpenRead(Path.Combine(Source, name, "bin", Configuration, Target, name + ".dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        return [.. metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))];
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<(int ExitCode, string Output)> DotNetAsync(string project, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.Combine(_root, project),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["NUGET_PACKAGES"] = Path.Combine(_root, "packages");
        start.Environment["LATTICE_BUILD_MARKER"] = Path.Combine(_root, "model-executed");


        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var standard = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await standard + Environment.NewLine + await errors);
    }

    private static string FindSource(DirectoryInfo directory)
    {
        for (var at = directory; at is not null; at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Moltaro.Lattice.slnx")))
            {
                return at.FullName;
            }
        }

        throw new InvalidOperationException("Cannot find Moltaro.Lattice.slnx above the tests.");
    }
}
