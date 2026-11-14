namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed class InjectedFailureException : Exception
{
    public InjectedFailureException() : base("Deliberate prototype fault.") { }
}
