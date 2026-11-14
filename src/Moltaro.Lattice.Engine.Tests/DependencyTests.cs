using System.Reflection;

namespace Moltaro.Lattice.Engine.Tests;

// What the built engine assembly depends on: the contract, and the index package at exactly the
// version the evidence was measured on (docs/decisions/index-structures.md). Two checks, because
// each sees a case the other cannot.
// - The references of the built assembly name every assembly its code uses, also one of a
//   package marked PrivateAssets="all", which no consumer receives. The compiler leaves out a
//   reference that no code uses: while the engine has no code, the assembly references neither
//   the contract nor Arc.Collections, so this check allows them but cannot require them.
// - The dependency manifest that the build writes next to this test assembly lists every package
//   and project reference that a referencing project receives, used or not. It does not list a
//   package marked PrivateAssets="all".
public sealed class DependencyTests
{
    [Fact]
    public void The_engine_assembly_references_only_the_contract_Arc_Collections_and_assemblies_of_dotnet()
    {
        var engine = Assembly.Load("Moltaro.Lattice.Engine");
        string[] allowed = ["Moltaro.Lattice", "Arc.Collections"];

        var others = engine.GetReferencedAssemblies()
            .Where(reference => !allowed.Contains(reference.Name) && !BuiltDependencies.IsPartOfDotnet(reference))
            .Select(reference => reference.FullName);

        Assert.Empty(others);
    }

    [Fact]
    public void The_engine_depends_only_on_the_contract_and_on_Arc_Collections_2_2_0()
    {
        var dependencies = BuiltDependencies.Recorded(typeof(DependencyTests).Assembly, "Moltaro.Lattice.Engine");

        Assert.Equal(["Arc.Collections", "Moltaro.Lattice"], dependencies.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("2.2.0", dependencies["Arc.Collections"]);
    }
}
