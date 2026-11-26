using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine;

// What a test of the engine gives an instance besides its options (exception X3,
// docs/work/exceptions.md), through EngineDatabase.Create(options, inputs). A test sets only what it
// needs; each member left at its default is what an instance made by Create(options) uses, so an instance made
// without inputs pays nothing for them.
internal sealed class EngineTestInputs
{
    // The options of the arena, so that a test can bound the arena to a few segments. When they are
    // given they are the options of the arena, and LatticeDatabaseOptions.MemoryLimit is not read;
    // EngineCounters.MemoryLimit reports the limit in force. Tests of a small arena use these; tests of
    // the option use the public open. Null: the arena takes its limit from the options of the database.
    public ArenaOptions? Arena { get; init; }

    // The allocator through which the arena obtains and returns its native memory, so that a test can
    // count every native byte the database obtained and returned. Null: NativeAllocator itself.
    public NativeAllocator? Allocator { get; init; }

    // The seed with which the primary indexes and the text table compute every hash, so that a test can
    // build keys that share one hash under one seed and show them spread under another, and give a child
    // another seed than its master. Null: the instance draws its own when it opens.
    public ulong? IndexSeed { get; init; }

    // The persistence step of the commit stage, so that a test can hold a commit until it releases it
    // (IPersistence). Null: the persistence of MemoryOnly, which completes at once.
    public IPersistence? Persistence { get; init; }

    // The source of the timestamps a read takes for its time limit, in ticks of Stopwatch, so that a test
    // decides how much time a step spends inside the engine, and when, without the clock of the machine and
    // without waiting (Entities.ReadSettings). It is called on the thread of the read, inside the step, and
    // must neither wait nor call the database. Null: Stopwatch.GetTimestamp.
    public Func<long>? ReadTimestamps { get; init; }

    // The monotonic clock and timers of pending position waits. A test can advance time while delaying
    // timer delivery, independently of the publishing thread. Null: TimeProvider.System.
    public TimeProvider? PositionWaitTime { get; init; }

    // The monotonic clock for collection capacity eligibility. Null: TimeProvider.System.
    public TimeProvider? MaintenanceTimeProvider { get; init; }

    // Invoked outside the state lock after Closed becomes visible and before close notifies the
    // position waiters. Tests can hold the notification to observe operations at that boundary.
    public Action? AfterCloseStatePublication { get; init; }

    // Invoked before the retirement queue allocates to trim itself in the final drain,
    // after the close has joined operations and reads. It can stand in for allocation failure.
    public Action? BeforeCloseRetiredTrim { get; init; }

    // Invoked with a maintenance attempt counted by the close barrier, after observing Ready
    // and before trying the lock of the writes. Tests can hold an attempt that close may overtake.
    public Action? BeforeMaintenanceWrites { get; init; }

    // Under Writes, immediately before a missing destination page is allocated during text migration.
    public Action? BeforeTextMigrationPageAllocation { get; init; }

    // Under Writes, just before allocating the first automatic root reclamation cursor.
    public Action? BeforeRootReclamationAllocation { get; init; }

    // Lets a test drive the ordinary bounded turn without the background cadence.
    public bool DisableBackgroundMaintenance { get; init; }

    // Invoked after Ready, outside the writes lock and close barrier, before the cadence waits.
    public Action<CancellationToken>? BeforeMaintenanceWait { get; init; }

    // Invoked with the completed sample outside the writes lock and close barrier.
    public Action<MaintenanceTurnSample>? ObserveMaintenanceTurn { get; init; }

    // Invoked after the fault is marked under the lock of the writes and that lock is released,
    // before the database finishes marking the fault and notifying position waiters.
    public Action? BeforeFaultNotification { get; init; }
}
