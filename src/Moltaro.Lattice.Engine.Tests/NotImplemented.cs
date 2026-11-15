namespace Moltaro.Lattice.Engine.Tests;

// The reason for which tests of Epic 1, sprint S07 in this project are skipped: the engine stores a
// String key compared without case and a schema of the UTF-16 encoding from that sprint on. The
// implementation removes every skip that names it, and the tests have to pass as they are written;
// then this type goes too.
internal static class NotImplemented
{
    public const string EngineStorage = "The key types and declared values that the engine stores from Epic 1, sprint S07, are not implemented yet.";
}
