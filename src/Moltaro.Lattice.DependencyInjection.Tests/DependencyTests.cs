using Moltaro.Lattice.TestSupport.Dependencies;

namespace Moltaro.Lattice.DependencyInjection.Tests;

// What the registration in a container references besides the contract and the engine: one package,
// Microsoft.Extensions.Hosting.Abstractions, where IHostedService, the .NET contract of a background
// service, lives. An integration is a thin assembly of its own (AGENTS.md, principle 11), and its package
// brings its users nothing else. The project file is read, which shows every package the project
// references, also one that no code uses yet.
public sealed class DependencyTests
{
    [Fact]
    public void The_registration_project_references_no_package_but_the_abstractions_of_hosting()
    {
        var packages = BuiltDependencies.ReferencedPackages(typeof(DependencyTests).Assembly, "Moltaro.Lattice.DependencyInjection");

        Assert.Equal(["Microsoft.Extensions.Hosting.Abstractions"], packages);
    }
}
