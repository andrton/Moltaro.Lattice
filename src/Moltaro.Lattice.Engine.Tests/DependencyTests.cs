using System.Text.Json;

namespace Moltaro.Lattice.Engine.Tests;

// What the built engine assembly depends on: the contract, and the index package at exactly the
// version the evidence was measured on (docs/decisions/index-structures.md). The compiler leaves
// out of an assembly every reference that no code uses, so the assembly's own list of references
// does not show them while the engine has no code. The dependency manifest that the build writes
// next to this test assembly lists every package and project the engine references, used or not.
public sealed class DependencyTests
{
    [Fact]
    public void The_engine_depends_only_on_the_contract_and_on_Arc_Collections_2_2_0()
    {
        var dependencies = BuiltDependencies("Moltaro.Lattice.Engine");

        Assert.Equal(["Arc.Collections", "Moltaro.Lattice"], dependencies.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("2.2.0", dependencies["Arc.Collections"]);
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
