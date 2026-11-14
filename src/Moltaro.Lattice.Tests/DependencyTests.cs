using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moltaro.Lattice.Tests;

// What the built contract assembly depends on: nothing outside .NET. Two checks, because each
// sees a case the other cannot.
// - The references of the built assembly name every assembly its code uses, also one of a
//   package marked PrivateAssets="all", which no consumer receives. The compiler leaves out a
//   reference that no code uses, so this check does not see a package that is not used yet.
// - The dependency manifest that the build writes next to this test assembly lists every package
//   and project reference that a referencing project receives, used or not. It does not list a
//   package marked PrivateAssets="all".
// Neither sees a package marked PrivateAssets="all" that no code uses: nothing of it reaches the
// built assembly or its package.
public sealed class DependencyTests
{
    [Fact]
    public void The_contract_assembly_references_only_assemblies_of_dotnet()
    {
        var contract = Assembly.Load("Moltaro.Lattice");

        var outsideDotnet = contract.GetReferencedAssemblies()
            .Where(reference => !IsPartOfDotnet(reference))
            .Select(reference => reference.FullName);

        Assert.Empty(outsideDotnet);
    }

    [Fact]
    public void The_contract_depends_on_no_package()
    {
        var dependencies = BuiltDependencies("Moltaro.Lattice");

        Assert.Empty(dependencies);
    }

    // An assembly is part of .NET when the shared framework this test runs on,
    // Microsoft.NETCore.App, contains it in the referenced version or a later one. A package
    // cannot place an assembly there, and a package that ships a later version of a framework
    // assembly is outside .NET as well.
    private static bool IsPartOfDotnet(AssemblyName reference)
    {
        var path = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), reference.Name + ".dll");

        return File.Exists(path) && AssemblyName.GetAssemblyName(path).Version >= reference.Version;
    }

    // The packages and projects, with their versions, that the build recorded for one project.
    private static Dictionary<string, string> BuiltDependencies(string projectName)
    {
        var manifestPath = Path.ChangeExtension(typeof(DependencyTests).Assembly.Location, ".deps.json");
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
}
