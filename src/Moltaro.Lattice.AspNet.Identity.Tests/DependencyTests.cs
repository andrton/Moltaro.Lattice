using Moltaro.Lattice.TestSupport.Dependencies;

namespace Moltaro.Lattice.AspNet.Identity.Tests;

// What the storage for ASP.NET Core Identity references besides the contract: one package,
// Microsoft.Extensions.Identity.Stores, where the store interfaces of Identity live, which it
// implements. An integration is a thin assembly of its own (AGENTS.md, principle 11), and its package
// brings its users nothing else. The project file is read, which shows every package the project
// references, also one that no code uses yet.
public sealed class DependencyTests
{
    [Fact]
    public void The_identity_storage_project_references_no_package_but_the_stores_of_identity()
    {
        var packages = BuiltDependencies.ReferencedPackages(typeof(DependencyTests).Assembly, "Moltaro.Lattice.AspNet.Identity");

        Assert.Equal(["Microsoft.Extensions.Identity.Stores"], packages);
    }
}
