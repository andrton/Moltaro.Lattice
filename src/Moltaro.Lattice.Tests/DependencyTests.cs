using System.Reflection;

namespace Moltaro.Lattice.Tests;

// What the built contract assembly depends on: nothing outside .NET (AGENTS.md, principle 11). Three
// checks, because each sees a case the others cannot.
// - The references of the built assembly name every assembly its code uses, also one of a
//   package marked PrivateAssets="all", which no consumer receives. The compiler leaves out a
//   reference that no code uses, so this check does not see a package that is not used yet.
// - The dependency manifest that the build writes next to this test assembly lists every package
//   and project reference that a referencing project receives, used or not. It does not list a
//   package marked PrivateAssets="all".
// - The project file shows a package that neither of the two above shows: one marked
//   PrivateAssets="all" that no code uses yet.
public sealed class DependencyTests
{
    [Fact]
    public void The_contract_assembly_references_only_assemblies_of_dotnet()
    {
        var contract = Assembly.Load("Moltaro.Lattice");

        var outsideDotnet = contract.GetReferencedAssemblies()
            .Where(reference => !BuiltDependencies.IsPartOfDotnet(reference))
            .Select(reference => reference.FullName);

        Assert.Empty(outsideDotnet);
    }

    [Fact]
    public void The_contract_depends_on_no_package()
    {
        var dependencies = BuiltDependencies.Recorded(typeof(DependencyTests).Assembly, "Moltaro.Lattice");

        Assert.Empty(dependencies);
    }

    [Fact]
    public void The_contract_project_references_no_package()
    {
        Assert.Empty(BuiltDependencies.ReferencedPackages(typeof(DependencyTests).Assembly, "Moltaro.Lattice"));
    }
}
