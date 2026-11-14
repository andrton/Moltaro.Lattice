using System.Text.Json;

namespace Moltaro.Lattice.Tests;

// What the built contract assembly depends on. The compiler leaves out of an assembly every
// reference that no code uses, so the assembly's own list of references does not show a package
// that was added but is not used yet. The dependency manifest that the build writes next to this
// test assembly lists every package and project the contract references, used or not.
public sealed class DependencyTests
{
    [Fact]
    public void The_contract_depends_on_no_package()
    {
        var dependencies = BuiltDependencies("Moltaro.Lattice");

        Assert.Empty(dependencies);
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
