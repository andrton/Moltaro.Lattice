namespace Moltaro.Lattice.Tests;

// The implementation a scenario test runs on; see TestDatabases. Public because it is the parameter
// of public test methods.
public enum DatabaseImplementationEnum
{
    // The managed placeholder of the core library, which LatticeDatabase opens until Epic 1,
    // sprint S12.
    Placeholder,

    // The engine database of Moltaro.Lattice.Engine.
    Engine,
}
