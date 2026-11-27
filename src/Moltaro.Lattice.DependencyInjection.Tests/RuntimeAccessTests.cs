using System.Reflection;
using Moltaro.Lattice.TestSupport.Dependencies;

namespace Moltaro.Lattice.DependencyInjection.Tests;

// The registration in a container carries no UnsafeAccessor aimed at a type outside the shipped
// assemblies (RuntimeAccess).
public sealed class RuntimeAccessTests
{
    [Fact]
    public void The_registration_aims_no_UnsafeAccessor_at_a_type_outside_the_shipped_assemblies()
    {
        Assert.Empty(RuntimeAccess.AccessorsAimedOutside(Assembly.Load("Moltaro.Lattice.DependencyInjection")));
    }
}
