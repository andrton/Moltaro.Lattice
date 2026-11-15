namespace Moltaro.Lattice.Tests;

// The reason for which tests of this project are skipped. The bound of one String or Binary value
// of decision O19 is in the contract: a write of an entity with a larger value is refused with
// ValidationFailed and the field named. The engine still refuses such a value with WriteRejected.
// The implementation removes every skip that names this reason, and the tests have to pass as they
// are written; then this type goes too.
internal static class NotImplemented
{
    public const string ValueBound = "The bound of one value of decision O19, ValidationFailed with the field named, is not implemented yet.";
}
