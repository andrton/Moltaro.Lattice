using System.Reflection;
using Moltaro.Lattice.TestSupport.Dependencies;

namespace Moltaro.Lattice.Engine.Tests;

// No shipped code reads or writes private members of the runtime or mirrors its private layout
// (RuntimeAccess): neither the contract nor the engine carries an UnsafeAccessor aimed at a type
// outside the shipped assemblies. The registration and the Identity store are checked in their own
// test projects; the generator targets netstandard2.0, which has no UnsafeAccessor.
public sealed class RuntimeAccessTests
{
    [Fact]
    public void The_contract_and_the_engine_aim_no_UnsafeAccessor_at_a_type_outside_the_shipped_assemblies()
    {
        Assert.Empty(RuntimeAccess.AccessorsAimedOutside(Assembly.Load("Moltaro.Lattice")));
        Assert.Empty(RuntimeAccess.AccessorsAimedOutside(Assembly.Load("Moltaro.Lattice.Engine")));
    }
}
