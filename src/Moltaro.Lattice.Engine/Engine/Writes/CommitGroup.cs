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
// The commit stage has one group, which it forms anew for every commit (WriteCoordinator), so that a
// commit allocates nothing for it: the storage step reads the group during its call and until the
// task it returned completes, and the stage forms the next group only after that.
internal sealed class CommitGroup
{
    public const int MaxOperations = 32;

    public const long MaxEncodedSize = 8L * 1024 * 1024;

    private readonly List<PreparedWrite> _writes = new(MaxOperations);

    public IReadOnlyList<PreparedWrite> Writes => _writes;

    // The sum of PreparedWrite.EncodedSize of the writes of the group.
    public long EncodedSize { get; private set; }

    // Takes the write into the group when it fits: when the group is empty, or holds fewer than
    // MaxOperations writes and the bytes of the write keep it within MaxEncodedSize. False otherwise,
    // and the group is as it was.
    public bool TryAdd(PreparedWrite write)
    {
        if (_writes.Count > 0 && (_writes.Count == MaxOperations || EncodedSize + write.EncodedSize > MaxEncodedSize))
        {
            return false;
        }

        _writes.Add(write);
        EncodedSize += write.EncodedSize;

        return true;
    }

    // Empties the group for the next commit.
    public void Clear()
    {
        _writes.Clear();
        EncodedSize = 0;
    }
}
