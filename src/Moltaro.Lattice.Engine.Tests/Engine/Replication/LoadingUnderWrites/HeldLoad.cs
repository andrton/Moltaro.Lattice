using Moltaro.Lattice.TestSupport.Replication;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.LoadingUnderWrites;

// The database of the tests of a load under writes, and a child held in the middle of its load by a link
// that holds back the rest of what the master sends (HeldLink), so that a test writes on the master in
// the gap and then lets the load go on.
//
// The master holds 16 small pieces, 1 to 16, and then 32 pieces of 1 MiB, 17 to 48, inserted in that
// order into an empty master, so that the capture, which walks the slots of the root table in their
// order, meets them in that order: an empty master gives its slots in the order of the inserts. When the
// link holds, after HeldLink.Passed bytes:
// - the small pieces have been copied: they travel in the first Blocks frame, well within what the link
//   passes (Copied);
// - the last pieces have not been copied yet: more than 28 MiB lie before them, far more than the fixed
//   receive buffer of the link (LoopbackLink.ReceiveBuffer) and the send buffer of the master hold, so
//   the capture waits in a send long before it reaches them (NotCopied).
// Which of the large pieces before those the capture has copied depends on those buffers, and no test
// relies on it.
internal sealed class HeldLoad : IAsyncDisposable
{
    public const int Small = 16;

    public const int Large = 32;

    // The pieces the capture has copied when the link holds.
    public static readonly long[] Copied = [.. Enumerable.Range(1, Small).Select(id => (long)id)];

    // Pieces the capture has not copied yet when the link holds.
    public static readonly long[] NotCopied = [Small + Large - 2, Small + Large - 1, Small + Large];

    private readonly HeldLink _link;

    private readonly Task _opening;

    private HeldLoad(HeldLink link, EngineDatabase child, Task opening)
    {
        _link = link;
        Child = child;
        _opening = opening;
    }

    // The child, Loading while the link holds, and Ready once ReleaseAsync has returned.
    public EngineDatabase Child { get; }

    // Inserts the database of these tests on an empty master.
    public static async Task InsertAsync(EngineDatabase master)
    {
        await Pieces.InsertAsync(master, 1, Small, dataBytes: 16);
        await Pieces.InsertAsync(master, Small + 1, Large, dataBytes: 1024 * 1024);
    }

    // Starts a child of the master through a link and returns once the link holds and the child is
    // Loading. The child has an open timeout far beyond the bound and the inputs given.
    public static async Task<HeldLoad> StartAsync(EngineDatabase master, EngineTestInputs? inputs = null)
    {
        var link = HeldLink.Start(master.ListenUri!);
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound), inputs ?? new EngineTestInputs());
        var opening = child.OpenAsync().AsTask();

        await link.HeldAsync();
        await MasterAndChild.EventuallyAsync(() => child.State == LatticeDatabaseStateEnum.Loading);

        return new HeldLoad(link, child, opening);
    }

    // Lets the rest of the load through and returns once the child is Ready.
    public async Task ReleaseAsync()
    {
        _link.Release();
        await _opening.WaitAsync(Bound);
    }

    public async ValueTask DisposeAsync()
    {
        await Child.DisposeAsync();
        await _link.DisposeAsync();
    }
}
