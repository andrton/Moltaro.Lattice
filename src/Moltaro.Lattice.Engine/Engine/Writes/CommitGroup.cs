namespace Moltaro.Lattice.Engine.Writes;

// A short run of prepared writes that share one storage commit. A batch is never split across two
// (brief of Epic 1, sprint S09, "Types and members of S09").
//
// A group is formed from the writes that are prepared when the commit stage becomes free: at most
// MaxOperations writes or MaxEncodedSize bytes of PreparedWrite.EncodedSize. A write larger than
// MaxEncodedSize forms a group alone, so the size of a group bounds what accumulates and never
// refuses a write. The commit stage never waits for a group to fill: a delay to fill a group belongs
// to the durable storage step and is decided with its sprint, S27 (brief of S09, the additions of
// 15 and 16 November 2026). That is why the MaxDelay of the first listing is not built.
//
// The writes of a group take consecutive positions from CommitOutcome.FirstSequence, in the order
// of Writes, which is the order in which the commit stage took them (ICommitStorage).
//
// The contract of S09 names this type and these members. Writes is empty until the implementer of
// S09 gives the type its constructor and fills it.
internal sealed class CommitGroup
{
    public const int MaxOperations = 32;

    public const long MaxEncodedSize = 8L * 1024 * 1024;

    public IReadOnlyList<PreparedWrite> Writes { get; } = [];
}
