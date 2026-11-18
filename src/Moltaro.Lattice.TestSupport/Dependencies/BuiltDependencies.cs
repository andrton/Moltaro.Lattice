using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace Moltaro.Lattice.TestSupport.Dependencies;

// What a built assembly of the solution depends on, for the dependency tests. Each of those tests
// says which cases its check sees and which it does not.
internal static class BuiltDependencies
{
    // The packages and projects, with their versions, that the build recorded for one project in
    // the dependency manifest it writes next to the test assembly that asks.
    public static Dictionary<string, string> Recorded(Assembly testAssembly, string projectName)
    {
        var manifestPath = Path.ChangeExtension(testAssembly.Location, ".deps.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        var runtimeTarget = manifest.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        var libraries = manifest.RootElement.GetProperty("targets").GetProperty(runtimeTarget);
        var project = libraries.EnumerateObject().Single(library => library.Name.StartsWith(projectName + "/", StringComparison.Ordinal));

        // The build writes no "dependencies" for a project that has none.
        if (!project.Value.TryGetProperty("dependencies", out var dependencies))
        {
            return new Dictionary<string, string>();
        }

        return dependencies.EnumerateObject().ToDictionary(dependency => dependency.Name, dependency => dependency.Value.GetString()!);
    }

    // The packages that the project of the product solution names references in its project file, by
    // their names. The file is read where the build of the test assembly that asks read it: beside the
    // solution file nearest above the directory of that assembly, which lies inside its project, so
    // inside src. A project file shows a package that neither the references of the built assembly nor
    // the manifest shows: one marked PrivateAssets="all" that no code uses yet.
    public static IReadOnlyList<string> ReferencedPackages(Assembly testAssembly, string projectName)
    {
        var solution = SolutionAbove(testAssembly);
        var project = XDocument.Load(Path.Combine(Path.GetDirectoryName(solution)!, projectName, projectName + ".csproj"));

        return [.. project.Descendants("PackageReference").Select(package => (string)package.Attribute("Include")!)];
    }

    // An assembly is part of .NET when the shared framework this test runs on,
    // Microsoft.NETCore.App, contains it in the referenced version or a later one. A package
    // cannot place an assembly there, and a package that ships a later version of a framework
    // assembly is outside .NET as well.
    public static bool IsPartOfDotnet(AssemblyName reference)
    {
        var path = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), reference.Name + ".dll");

        return File.Exists(path) && AssemblyName.GetAssemblyName(path).Version >= reference.Version;
    }

    private static string SolutionAbove(Assembly testAssembly)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(testAssembly.Location)!);

        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "Moltaro.Lattice.slnx");

            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("No Moltaro.Lattice.slnx lies above the test assembly.");
    }
}
