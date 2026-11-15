namespace Moltaro.Lattice.Engine.Tests;

// The reason for which tests of this project are skipped. The bound of one String or Binary value
// of decision O19 is in the contract: a value above it is refused by its Set… member of RootWriter
// with ValidationFailed and the field named. The code still refuses such a value with WriteRejected
// at Complete. The implementation removes every skip that names this reason, and the tests have to
// pass as they are written; then this type goes too.
internal static class NotImplemented
{
    public const string ValueBound = "The bound of one value of decision O19, ValidationFailed from the Set member, is not implemented yet.";
}
