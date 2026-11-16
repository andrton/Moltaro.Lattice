namespace Moltaro.Lattice.Engine.Writes;

// One write as the commit stage sees it: prepared, with everything its publication needs reserved
// (brief of Epic 1, sprint S09, "Types and members of S09").
//
// EncodedSize is the bytes of the slots the write prepared in the arena: the root and every block of
// its values (brief of S09, "What is counted"). It is a long, and so is every sum of it, because no
// rule bounds the size of an entity (decision O19). A write that waits and is not prepared yet holds
// nothing in the engine and has no PreparedWrite.
//
// The contract of S09 names this type and its one member. What else it holds and how it is made are
// the implementer's: the implementer of S09 gives it its constructor and fills it.
internal sealed class PreparedWrite
{
    public long EncodedSize { get; }
}
