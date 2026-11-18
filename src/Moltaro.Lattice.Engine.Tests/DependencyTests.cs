using System.Reflection;

namespace Moltaro.Lattice.Engine.Tests;

// What the built engine assembly depends on: the contract and nothing else (AGENTS.md, principle 11: the
// engine depends on the contract and on no package; its structures are its own). Three checks, because
// each sees a case the others cannot:
// - The references of the built assembly name every assembly its code uses, also one of a package
//   marked PrivateAssets="all", which no consumer receives.
// - The dependency manifest that the build writes next to this test assembly lists every package and
//   project reference that a referencing project receives, used or not. It does not list a package
//   marked PrivateAssets="all".
// - The project file shows a package that neither of the two above shows: one marked
//   PrivateAssets="all" that no code uses yet.
public sealed class DependencyTests
{
    [Fact]
    public void The_engine_assembly_references_only_the_contract_and_assemblies_of_dotnet()
    {
        var engine = Assembly.Load("Moltaro.Lattice.Engine");

        var others = engine.GetReferencedAssemblies()
            .Where(reference => reference.Name != "Moltaro.Lattice" && !BuiltDependencies.IsPartOfDotnet(reference))
            .Select(reference => reference.FullName);

        Assert.Empty(others);
    }

    [Fact]
    public void The_engine_depends_only_on_the_contract()
    {
        var dependencies = BuiltDependencies.Recorded(typeof(DependencyTests).Assembly, "Moltaro.Lattice.Engine");

        Assert.Equal(["Moltaro.Lattice"], dependencies.Keys);
    }

    [Fact]
    public void The_engine_project_references_no_package()
    {
        Assert.Empty(BuiltDependencies.ReferencedPackages(typeof(DependencyTests).Assembly, "Moltaro.Lattice.Engine"));
    }
}
