using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Automatic movement uses spare slots in earlier regions, without retaining more than one
// largest slot of old copies (arena-protocol.md). These layouts come from ordinary commits
// and real child application. A counted ordinary worker turn chooses all physical addresses.
public sealed class AutomaticRelocationTests
{
    private const int WorkspaceLimit = Arena.MaxBlockSize;
    private const int Big = 16 * 1024;
    private const int Medium = 8 * 1024;
    private const int Small = 4 * 1024;
    private const int TurnBound = 256;
    private const int FillerCount = 64;
    private static readonly LatticeSchema Schema = MakeSchema();

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_worker_reports_the_actual_fixed_root_shape_only_after_a_move(LatticeRoleEnum role)
    {
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(null,
            observe: role == LatticeRoleEnum.Master ? samples.Add : null));
        await InsertAsync(master, "Big", 1, Arena.MaxBlockSize / Big + 1);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(null,
            observe: role == LatticeRoleEnum.Child ? samples.Add : null));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var roots = Locations(node).OrderBy(location => location.Key).ToArray();
        var source = roots[^1];
        var hole = roots[^2];
        Assert.True(source.Address.Segment > hole.Address.Segment);

        RunTurn(node);
        var empty = samples[^1];
        Assert.Equal(default, empty.Source);
        Assert.Equal(0, empty.SlotBytes);
        Assert.Equal(default, empty.SourceKind);
        Assert.Equal((ushort)0, empty.SourceFlags);
        Assert.Equal(default, empty.SourceDescriptorKind);

        await DeleteAsync(master, [hole]);
        await FollowAsync(master, node);
        node.DrainRetired();
        RunUntil(node, () => Find(node, source).Address != source.Address);

        var moved = Assert.Single(samples, sample => sample.SlotBytes != 0);
        Assert.Equal(source.Address, moved.Source);
        Assert.Equal(hole.Address, moved.Destination);
        Assert.Equal(Big, moved.SlotBytes);
        Assert.Equal(BlockKindEnum.Root, moved.SourceKind);
        Assert.Equal((ushort)0, moved.SourceFlags);
        Assert.Equal(default, moved.SourceDescriptorKind);

        RunTurn(node);
        ExpectNoSourceReport(samples[^1]);
    }

    internal static void ExpectNoSourceReport(MaintenanceTurnSample sample)
    {
        Assert.True(sample.Admitted);
        Assert.Equal((default(ArenaAddress), default(ArenaAddress), 0), (sample.Source, sample.Destination, sample.SlotBytes));
        Assert.Equal((default(BlockKindEnum), (ushort)0, default(DescriptorKindEnum)),
            (sample.SourceKind, sample.SourceFlags, sample.SourceDescriptorKind));
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task Mixed_slot_sizes_use_at_most_one_largest_slot_while_old_copies_are_held(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null, regions: 5));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator, regions: 5))
            : null;
        var node = child ?? master;
        var (targets, mediumWaits, _) = await StageWorkspaceAsync(master, node, exactLimit: true);
        Assert.Equal(WorkspaceLimit, targets.Sum(target => target.SlotBytes));
        var target = targets[0];
        var revisions = targets.ToDictionary(location => location, location => DemandRecord(node, location));
        var revision = revisions[target];
        var reading = HoldView(node, target, revision, out var pause);
        var returns = allocator.Returned;
        var requests = allocator.Requests;
        var initial = node.Counters.Arena;
        using (pause)
        {
            await pause.EnteredAsync(reading);
            var position = node.Position;
            FillWorkspace(node, targets, ordinaryBytes: 0, allocator, requests, returns, WorkspaceLimit);
            Assert.Equal(position, node.Position);
            Assert.All(targets, location => DemandMoved(node, location, revisions[location]));

            var blocked = mediumWaits[0];
            await EnableSourceAsync(master, node, mediumWaits);
            CheckVacancies(node, blocked, targets);
            position = node.Position;
            RunQuietTurns(node, () =>
            {
                Assert.Equal(blocked.Address, Find(node, blocked).Address);
                Assert.All(mediumWaits.Skip(1), retired => Assert.Equal(retired.Handle, retired.Arena.Header(retired.Address).Root));
                Assert.Equal(WorkspaceLimit, Charge(node));
                CheckPinnedTurn(node, targets, (mediumWaits.Length - 1) * Medium, allocator, requests, returns);
            });
            Assert.Equal(position, node.Position);
            Assert.Equal(initial.Segments, node.Counters.Arena.Segments);

            pause.Release();
            await reading.WaitAsync(Bound);
        }

        var positionAfterDelete = node.Position;
        RunUntil(node, () => Find(node, mediumWaits[0]).Address != mediumWaits[0].Address);
        DemandMoved(node, mediumWaits[0]);
        RunUntil(node, () => node.Counters.Arena.RetiredBytes == 0 && allocator.Returned == returns + 1);
        Assert.Equal(0, Charge(node));
        Assert.All(targets, DemandFreed);
        Assert.All(mediumWaits.Skip(1), DemandFreed);
        Assert.Equal(positionAfterDelete, node.Position);
        Assert.Equal(revision, DemandRecord(node, target));
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        Assert.Equal(allocator.Outstanding, node.Counters.Arena.Segments);
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task At_sixty_KiB_the_worker_skips_an_eight_KiB_slot_and_uses_a_four_KiB_slot(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null, regions: 5));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator, regions: 5))
            : null;
        var node = child ?? master;
        var (targets, mediumWaits, smallWaits) = await StageWorkspaceAsync(master, node, exactLimit: false);
        Assert.Equal(WorkspaceLimit - Small, targets.Sum(target => target.SlotBytes));
        var target = targets[0];
        var revisions = targets.ToDictionary(location => location, location => DemandRecord(node, location));
        var revision = revisions[target];
        var reading = HoldView(node, target, revision, out var pause);
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        using (pause)
        {
            await pause.EnteredAsync(reading);
            FillWorkspace(node, targets, ordinaryBytes: 0, allocator, requests, returns, WorkspaceLimit - Small);
            Assert.All(targets, location => DemandMoved(node, location, revisions[location]));

            await EnableSourceAsync(master, node, mediumWaits);
            CheckVacancies(node, mediumWaits[0], targets);
            var beforeRefusal = SnapshotEarlierVacancies(node, allocator, mediumWaits[0], targets);
            Assert.NotEmpty(beforeRefusal);
            RunQuietTurns(node, () =>
            {
                Assert.Equal(mediumWaits[0].Address, Find(node, mediumWaits[0]).Address);
                Assert.Equal(WorkspaceLimit - Small, Charge(node));
                CheckPinnedTurn(node, targets, (mediumWaits.Length - 1) * Medium, allocator, requests, returns);
            });

            var afterRefusal = SnapshotEarlierVacancies(node, allocator, mediumWaits[0], targets);
            Assert.Equal(beforeRefusal.Keys, afterRefusal.Keys);
            Assert.All(beforeRefusal, slot => Assert.Equal(slot.Value, afterRefusal[slot.Key]));

            await EnableSourceAsync(master, node, smallWaits);
            CheckVacancies(node, smallWaits[0], targets);
            var candidates = targets.Append(smallWaits[0]).ToArray();
            var ordinaryBytes = (mediumWaits.Length - 1) * Medium + (smallWaits.Length - 1) * Small;
            FillWorkspace(node, candidates, ordinaryBytes, allocator, requests, returns, WorkspaceLimit, () =>
                Assert.Equal(mediumWaits[0].Address, Find(node, mediumWaits[0]).Address));
            DemandMoved(node, smallWaits[0]);
            foreach (var removed in mediumWaits.Skip(1).Concat(smallWaits.Skip(1)))
            {
                _ = removed.Arena.Header(removed.Address);
            }

            pause.Release();
            await reading.WaitAsync(Bound);
        }

        // Ordinary cleanup returns the same real slots and their charge, irrespective of
        // whether the worker or the writer crosses the safe epoch first.
        node.DrainRetired();
        Assert.Equal(0, Charge(node));
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.All(targets, DemandFreed);
        DemandFreed(smallWaits[0]);
        RunUntil(node, () => Find(node, mediumWaits[0]).Address != mediumWaits[0].Address);
        DemandMoved(node, mediumWaits[0]);
        Assert.Equal(revision, DemandRecord(node, target));
        Assert.Equal(requests, allocator.Requests);
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master, false)]
    [InlineData(LatticeRoleEnum.Child, false)]
    [InlineData(LatticeRoleEnum.Master, true)]
    [InlineData(LatticeRoleEnum.Child, true)]
    public async Task The_worker_uses_an_earlier_vacancy_behind_the_head_and_keeps_a_segment_with_an_index_node(
        LatticeRoleEnum role, bool keepsIndex)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null, regions: 2, reserve: 0));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator, regions: 2, reserve: 0))
            : null;
        var node = child ?? master;
        var slots = Arena.MaxBlockSize / Medium;
        await InsertAsync(master, "Medium", 1, 3 * slots - 1);
        var type = keepsIndex ? "Indexed" : "Medium";
        await InsertAsync(master, type, 3 * slots, 1);
        await FollowAsync(master, node);
        var full = Locations(node);
        var earlier = full.Where(location => location.Key <= 2 * slots).ToArray();
        var later = full.Where(location => location.Key > 2 * slots).ToArray();
        var target = Assert.Single(later, location => location.Key == 3 * slots);
        Assert.Equal(Medium, target.SlotBytes);
        var earlierSegment = Assert.Single(earlier.Select(location => location.Address.Segment).Distinct());
        Assert.True(earlierSegment < target.Address.Segment);
        Assert.All(later, location => Assert.Equal(target.Address.Segment, location.Address.Segment));
        Assert.Equal(2, allocator.Outstanding);
        var index = keepsIndex ? IndexRoot(node, target) : default;
        if (keepsIndex)
        {
            Assert.Equal(target.Address.Segment, index.Segment);
            Assert.NotEqual(target.Address.Offset / Arena.MaxBlockSize, index.Offset / Arena.MaxBlockSize);
            DemandIndex(node, target);
        }

        // Free earlier holes first. Returning the first slot from the later full
        // region pushes that region to the class list head. It cannot receive its own move.
        var vacancies = earlier.Take(2).ToArray();
        await DeleteAsync(master, vacancies);
        await FollowAsync(master, node);
        node.DrainRetired();
        await DeleteAsync(master, later.Where(location => location != target));
        await FollowAsync(master, node);
        node.DrainRetired();
        Assert.Equal(RegionOf(target), HeadOfClass(target));
        Assert.All(vacancies, DemandFreed);
        var revision = DemandRecord(node, target);
        var position = node.Position;
        var requests = allocator.Requests;
        var returns = allocator.Returned;

        RunUntil(node, () => Find(node, target).Address != target.Address);
        Assert.Contains(Find(node, target).Address, vacancies.Select(location => location.Address));
        RunUntil(node, () => node.Counters.Arena.RetiredBytes == 0);
        DemandFreed(target);
        Assert.Equal(0, Charge(node));
        Assert.Equal(position, node.Position);
        Assert.Equal(revision, DemandRecord(node, target));
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns + (keepsIndex ? 0 : 1), allocator.Returned);
        Assert.Equal(keepsIndex ? 2 : 1, allocator.Outstanding);
        Assert.Equal(allocator.Outstanding, node.Counters.Arena.Segments);
        if (keepsIndex)
        {
            Assert.Equal(index, IndexRoot(node, target));
            DemandIndex(node, target);
        }
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task An_automatically_selected_root_waits_for_an_earlier_slot_and_finds_it_amid_head_changes(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null, reserve: 0));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator, reserve: 0))
            : null;
        var node = child ?? master;
        var slots = Arena.MaxBlockSize / Medium;
        await InsertAsync(master, "Medium", 1, 4 * slots);
        await FollowAsync(master, node);
        var regions = Locations(node).GroupBy(RegionOf).OrderBy(group => group.Key).Select(group => group.ToArray()).ToArray();
        Assert.Equal(4, regions.Length);
        Assert.All(regions, region => Assert.Equal(slots, region.Length));
        Assert.All(regions.SelectMany(region => region), location => Assert.Equal(Medium, location.SlotBytes));
        var target = regions[1][0];
        var revision = DemandRecord(node, target);
        await DeleteAsync(master, regions[1].Skip(1));
        await FollowAsync(master, node);
        node.DrainRetired();
        var staged = node.Counters.Arena;
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        RunQuietTurns(node, () =>
        {
            Assert.Equal(target.Address, Find(node, target).Address);
            Assert.Equal(staged, node.Counters.Arena);
            Assert.Equal(0, Charge(node));
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(returns, allocator.Returned);
        });

        var destination = regions[0][0];
        var left = regions[2][0];
        var right = regions[3][0];
        await DeleteAsync(master, [destination, left]);
        await FollowAsync(master, node);
        node.DrainRetired();
        long newKey = 4 * slots + 1;
        for (var turn = 0; turn < TurnBound && Find(node, target).Address == target.Address; turn++)
        {
            // Fill the only vacancy at the current later head, then free one in
            // a different later region. The same earlier slot stays available.
            Assert.Equal(RegionOf(left), HeadOfClass(left));
            await InsertAsync(master, "Medium", (int)newKey, 1);
            await FollowAsync(master, node);
            var inserted = Find(node, left with { Key = newKey++ });
            Assert.Equal(left.Address, inserted.Address);
            await DeleteAsync(master, [right]);
            await FollowAsync(master, node);
            node.DrainRetired();
            Assert.Equal(RegionOf(right), HeadOfClass(right));
            DemandFreed(destination);
            var position = node.Position;
            RunTurn(node);
            Assert.Equal(position, node.Position);
            left = right;
            right = inserted;
        }

        Assert.Equal(destination.Address, Find(node, target).Address);
        DemandMoved(node, target, revision);
        RunUntil(node, () => node.Counters.Arena.RetiredBytes == 0);
        DemandFreed(target);
        Assert.Equal(0, Charge(node));
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(3, allocator.Outstanding);
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task One_admitted_turn_reclaims_a_native_segment_and_moves_one_root_while_leaving_the_FIFO_bounded(LatticeRoleEnum role)
    {
        var expected = await PendingFillersAfterTurnAsync(role, direct: true);
        var observed = await PendingFillersAfterTurnAsync(role, direct: false);
        Assert.NotEmpty(expected.AtLimit);
        Assert.Single(expected.AtLimit.Except(expected.NextUnit));
        Assert.Equal(expected.AtLimit, observed.AtLimit);
        Assert.Equal(expected.NextUnit, observed.NextUnit);
    }

    private static async Task<(long[] AtLimit, long[] NextUnit)> PendingFillersAfterTurnAsync(LatticeRoleEnum role, bool direct)
    {
        var allocator = new CountingNativeAllocator();
        MaintenanceTurnSample? lastTurn = null;
        void Observe(MaintenanceTurnSample sample) => lastTurn = sample;
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema,
            Inputs(role == LatticeRoleEnum.Master ? allocator : null, reserve: 0,
                observe: role == LatticeRoleEnum.Master ? Observe : null));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator, reserve: 0, observe: Observe))
            : null;
        var node = child ?? master;
        var slots = Arena.MaxBlockSize / Medium;
        await InsertAsync(master, "Medium", 1, 3 * slots);
        await InsertAsync(master, "Filler", 1, FillerCount);
        await FollowAsync(master, node);
        var locations = Locations(node);
        var regions = locations.Where(location => location.Type == "Medium").GroupBy(RegionOf)
            .OrderBy(group => group.Key).Select(group => group.ToArray()).ToArray();
        var filler = locations.Where(location => location.Type == "Filler").ToArray();
        Assert.Equal(3, regions.Length);
        Assert.All(regions, region => Assert.Equal(slots, region.Length));
        Assert.Equal(FillerCount, filler.Length);
        Assert.Equal(4, allocator.Outstanding);
        var destination = regions[0].Skip(slots / 2).ToArray();
        var first = regions[1][0];
        var nextSource = regions[2].Take(slots / 2).ToArray();
        var next = nextSource[0];
        await DeleteAsync(master, destination.Concat(regions[1].Skip(1)).Concat(regions[2].Skip(slots / 2)));
        await FollowAsync(master, node);
        node.DrainRetired();
        Assert.Equal(0, QueueOf(node).Count);
        Assert.All(destination, DemandFreed);
        var firstRevision = DemandRecord(node, first);
        var nextRevision = DemandRecord(node, next);
        var reading = HoldView(node, first, firstRevision, out var pause);
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        using (pause)
        {
            await pause.EnteredAsync(reading);
            RunUntil(node, () => Find(node, first).Address != first.Address);
            var firstTurn = Assert.NotNull(lastTurn);
            var movedFirst = Find(node, first);
            Assert.Contains(movedFirst.Address, destination.Select(hole => hole.Address));
            ExpectMove(firstTurn, first, movedFirst);
            Assert.Equal(0, firstTurn.ReclamationWork);
            Assert.Equal(0, firstTurn.SegmentsReturned);
            Assert.Equal(0L, firstTurn.ReturnedNativeBytes);
            Assert.Equal(first.Handle, first.Arena.Header(first.Address).Root);
            Assert.Equal(Medium, firstTurn.RelocationBytes);

            await EnableSourceAsync(master, node, nextSource);
            await DeleteAsync(master, filler);
            await FollowAsync(master, node);
            Assert.Equal(nextSource.Length + FillerCount, QueueOf(node).Count);
            Assert.Equal(Medium, Charge(node));
            Assert.Equal(Medium + (nextSource.Length - 1) * Medium + filler.Sum(root => root.SlotBytes),
                node.Counters.Arena.RetiredBytes);
            Assert.Equal(returns, allocator.Returned);
            pause.Release();
            await reading.WaitAsync(Bound);
        }

        if (direct)
        {
            var work = 0;
            Inspect(node, parts => work = parts.Retired.DrainStep(AutomaticMaintenance.ReclamationWorkLimit));
            Assert.Equal(AutomaticMaintenance.ReclamationWorkLimit, work);
            Assert.Equal(returns + 1, allocator.Returned);
            DemandFreed(first);
            Assert.Equal(0, Charge(node));
            Assert.Equal(next.Address, Find(node, next).Address);
            return ObserveFillerBoundary(node, filler);
        }

        // The old relocation is the FIFO head. The following ordinary roots cannot
        // all be reclaimed in one turn, but the next relocation must still run.
        var position = node.Position;
        lastTurn = null;
        RunTurn(node);
        var completeTurn = Assert.NotNull(lastTurn);
        var movedNext = Find(node, next);
        Assert.Contains(movedNext.Address, destination.Select(hole => hole.Address));
        Assert.NotEqual(Find(node, first).Address, movedNext.Address);
        ExpectMove(completeTurn, next, movedNext);
        Assert.Equal(AutomaticMaintenance.ReclamationWorkLimit, completeTurn.ReclamationWork);
        Assert.Equal(1, completeTurn.SegmentsReturned);
        Assert.Equal(Arena.MaxBlockSize, completeTurn.ReturnedNativeBytes);
        Assert.Equal(returns + 1, allocator.Returned);
        Assert.Equal(allocator.Outstanding, node.Counters.Arena.Segments);
        Assert.Equal(requests, allocator.Requests);
        DemandFreed(first);
        Assert.Equal(next.Handle, next.Arena.Header(next.Address).Root);
        Assert.Equal(Medium, completeTurn.RelocationBytes);
        Assert.Equal(Medium, Charge(node));
        Assert.True(node.Counters.Arena.RetiredBytes > Medium);
        Assert.True(QueueOf(node).Count > 1);
        var pending = ObserveFillerBoundary(node, filler);
        Assert.NotEmpty(pending.AtLimit);
        Assert.Equal(position, node.Position);
        DemandMoved(node, first, firstRevision);
        DemandMoved(node, next, nextRevision);
        RunUntil(node, () => node.Counters.Arena.RetiredBytes == 0);
        DemandFreed(next);
        Assert.Equal(0, Charge(node));
        return pending;
    }

    private static (long[] AtLimit, long[] NextUnit) ObserveFillerBoundary(EngineDatabase node, Location[] filler)
    {
        var atLimit = PendingFillerKeys(filler);
        var work = 0;
        Inspect(node, parts => work = parts.Retired.DrainStep(1));
        Assert.Equal(1, work);
        return (atLimit, PendingFillerKeys(filler));
    }

    private static long[] PendingFillerKeys(Location[] filler) =>
        filler.Where(IsAllocated).Select(location => location.Key).Order().ToArray();

    [Theory]
    [InlineData(LatticeRoleEnum.Master, false)]
    [InlineData(LatticeRoleEnum.Child, false)]
    [InlineData(LatticeRoleEnum.Master, true)]
    [InlineData(LatticeRoleEnum.Child, true)]
    public async Task A_selection_turn_spends_its_bounded_search_work_and_a_later_turn_finds_the_earlier_slot(
        LatticeRoleEnum role, bool combinedBudget)
    {
        const int Limit = AutomaticMaintenance.SelectionWorkLimit;
        var regionCount = combinedBudget ? 3 * Limit / 4 + 4 : Limit + 3;
        var allocator = new CountingNativeAllocator();
        var observed = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema,
            Inputs(role == LatticeRoleEnum.Master ? allocator : null, reserve: 0,
                observe: role == LatticeRoleEnum.Master ? observed.Add : null, maxSegments: regionCount));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!,
                Inputs(allocator, reserve: 0, observe: observed.Add, maxSegments: regionCount))
            : null;
        var node = child ?? master;
        var slots = Arena.MaxBlockSize / Big;
        await InsertAsync(master, "Big", 1, regionCount * slots);
        await FollowAsync(master, node);
        node.DrainRetired();
        var regions = Locations(node).GroupBy(RegionOf).OrderBy(region => region.Key)
            .Select(region => region.ToArray()).ToArray();
        Assert.Equal(regionCount, regions.Length);
        Assert.All(regions, region => Assert.Equal(slots, region.Length));
        Assert.All(regions.SelectMany(region => region), location => Assert.Equal(Big, location.SlotBytes));
        Assert.Equal(regionCount, allocator.Outstanding);
        var vacancies = regions[^2].Skip(slots / 2).ToArray();
        var source = regions[^1][0];
        await DeleteAsync(master, vacancies);
        await FollowAsync(master, node);
        node.DrainRetired();
        await DeleteAsync(master, regions[^1].Skip(1));
        await FollowAsync(master, node);
        node.DrainRetired();
        var sourcePrefix = RegionOf(source).Segment;
        var destinationPrefix = RegionOf(vacancies[0]).Segment;
        if (combinedBudget)
        {
            // Each prefix fits on its own, so independent selection budgets
            // could publish in the first turn. The single turn budget cannot.
            Assert.True(sourcePrefix < Limit);
            Assert.True(destinationPrefix < Limit);
            Assert.True(sourcePrefix + destinationPrefix > Limit);
        }
        else
        {
            Assert.True(sourcePrefix > Limit);
            Assert.True(destinationPrefix > Limit);
        }
        Assert.Equal(RegionOf(source), HeadOfClass(source));
        Assert.All(vacancies, DemandFreed);
        var revision = DemandRecord(node, source);
        var before = Locations(node);
        var position = node.Position;
        var arena = node.Counters.Arena;
        RunTurn(node);
        var firstTurn = observed[^1];
        Assert.True(firstTurn.Admitted);
        Assert.Equal(AutomaticMaintenance.SelectionWorkLimit, firstTurn.SelectionWork);
        Assert.Equal(0, firstTurn.SlotBytes);
        if (combinedBudget)
        {
            Assert.True(firstTurn.CandidatesExamined > 0);
        }
        Assert.Equal(source.Address, Find(node, source).Address);
        Assert.Equal(arena, node.Counters.Arena);
        Assert.Equal(position, node.Position);
        Assert.All(vacancies, DemandFreed);

        RunUntil(node, () => Find(node, source).Address != source.Address);
        if (!combinedBudget)
        {
            // The long control must also yield after seeing the candidate and
            // before its earlier-vacancy search can have reached the target.
            Assert.Contains(observed.Skip(1), sample => sample.Admitted && sample.CandidatesExamined > 0 &&
                sample.SelectionWork == Limit && sample.SlotBytes == 0);
        }
        var current = Find(node, source);
        var moved = Assert.Single(observed, sample => sample.SlotBytes != 0);
        ExpectMove(moved, source, current);
        Assert.All(observed, sample => Assert.InRange(sample.SelectionWork, 0, AutomaticMaintenance.SelectionWorkLimit));
        Assert.Contains(current.Address, vacancies.Select(hole => hole.Address));
        var after = Locations(node);
        Assert.Equal(before.Select(record => (record.Key, record.Handle)), after.Select(record => (record.Key, record.Handle)));
        Assert.All(after, record => DemandRecord(node, record));
        Assert.Equal(revision, DemandRecord(node, source));
        Assert.Equal(position, node.Position);
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task The_Ready_worker_selects_and_moves_a_root_after_leaving_its_wait_boundary(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        using var waiting = new HeldEngineCallback();
        var holdOnce = 1;
        var moved = new TaskCompletionSource<MaintenanceTurnSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        void HoldFirstWait(CancellationToken _)
        {
            if (Interlocked.Exchange(ref holdOnce, 0) != 0)
            {
                waiting.Hold();
            }
        }

        void Observe(MaintenanceTurnSample sample)
        {
            if (sample.SlotBytes != 0)
            {
                moved.TrySetResult(sample);
            }
        }

        var inputs = Inputs(allocator, reserve: 0, observe: Observe, beforeWait: HoldFirstWait);
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema,
            role == LatticeRoleEnum.Master ? inputs : Inputs(null, reserve: 0));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs)
            : null;
        var node = child ?? master;
        try
        {
            await waiting.ReachedAsync();
            Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
            var slots = Arena.MaxBlockSize / Medium;
            await InsertAsync(master, "Medium", 1, 2 * slots);
            await FollowAsync(master, node);
            var regions = Locations(node).GroupBy(RegionOf).OrderBy(group => group.Key).Select(group => group.ToArray()).ToArray();
            Assert.Equal(2, regions.Length);
            var destination = regions[0][0];
            var source = regions[1][0];
            await DeleteAsync(master, [destination, .. regions[1].Skip(1)]);
            await FollowAsync(master, node);
            node.DrainRetired();
            DemandFreed(destination);
            var revision = DemandRecord(node, source);
            var position = node.Position;
            Assert.False(moved.Task.IsCompleted);
            Assert.Equal(source.Address, Find(node, source).Address);

            waiting.Release();
            var sample = await moved.Task.WaitAsync(Bound);
            var current = Find(node, source);
            ExpectMove(sample, source, current);
            Assert.Equal(destination.Address, current.Address);
            Assert.Equal(position, node.Position);
            DemandMoved(node, source, revision);
        }
        finally
        {
            waiting.Release();
        }
    }

    private static void ExpectMove(MaintenanceTurnSample sample, Location source, Location destination)
    {
        Assert.True(sample.Admitted);
        Assert.InRange(sample.CandidatesExamined, 1, sample.SelectionWork);
        Assert.InRange(sample.SelectionWork, 1, AutomaticMaintenance.SelectionWorkLimit);
        Assert.InRange(sample.ReclamationWork, 0, AutomaticMaintenance.ReclamationWorkLimit);
        Assert.Equal(source.Address, sample.Source);
        Assert.Equal(destination.Address, sample.Destination);
        Assert.Equal(source.SlotBytes, sample.SlotBytes);
    }

    private static bool IsAllocated(Location location)
    {
        try
        {
            return location.Arena.Header(location.Address).Root == location.Handle;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<(Location[] Targets, Location[] MediumWaits, Location[] SmallWaits)> StageWorkspaceAsync(
        EngineDatabase master, EngineDatabase node, bool exactLimit)
    {
        var bigSlots = Arena.MaxBlockSize / Big;
        var mediumSlots = Arena.MaxBlockSize / Medium;
        var smallSlots = Arena.MaxBlockSize / Small;
        // In each class, put the destinations in the first segment and all the
        // initially movable roots in the same second segment. They cannot select
        // one another as destinations, independently of the candidate order.
        await InsertAsync(master, "Big", 1, 2 * bigSlots);
        await InsertAsync(master, "Medium", 1, mediumSlots);
        await InsertAsync(master, "Small", 1, smallSlots);
        await InsertAsync(master, "Filler", 1, FillerCount);
        await InsertAsync(master, "Big", 2 * bigSlots + 1, 3 * bigSlots);
        await InsertAsync(master, "Medium", mediumSlots + 1, mediumSlots);
        await InsertAsync(master, "Small", smallSlots + 1, smallSlots);
        await InsertAsync(master, "Medium", 2 * mediumSlots + 1, mediumSlots);
        if (!exactLimit)
        {
            await InsertAsync(master, "Small", 2 * smallSlots + 1, smallSlots);
        }

        await FollowAsync(master, node);
        var full = Locations(node);
        var staged = new List<Location[]>();
        StageClass("Big", Big, 5, [2, 2, 1, 1, 1]);
        StageClass("Medium", Medium, 3, [4, exactLimit ? 2 : 1, 4]);
        StageClass("Small", Small, exactLimit ? 2 : 3, exactLimit ? [8, 0] : [8, 1, 8]);

        node.DrainRetired();
        Assert.Equal(0, QueueOf(node).Count);
        // This reservation is grown by deletes of actual entities of another class.
        // They retire together while a reader pins them, before any maintenance turn.
        var filler = full.Where(location => location.Type == "Filler").ToArray();
        Assert.Equal(FillerCount, filler.Length);
        var anchor = staged[2][0];
        var revision = DemandRecord(node, anchor);
        var filling = HoldView(node, anchor, revision, out var pause);
        using (pause)
        {
            await pause.EnteredAsync(filling);
            await DeleteAsync(master, filler);
            await FollowAsync(master, node);
            Assert.Equal(FillerCount, QueueOf(node).Count);
            pause.Release();
            await filling.WaitAsync(Bound);
        }

        node.DrainRetired();
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        var (count, capacity) = QueueOf(node);
        Assert.Equal(0, count);
        Assert.True(capacity >= FillerCount);
        var survivors = staged.SelectMany(group => group).ToHashSet();
        await DeleteAsync(master, full.Where(location => location.Type != "Filler" && !survivors.Contains(location)));
        await FollowAsync(master, node);
        node.DrainRetired();
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(staged.Sum(group => group.Length), Locations(node).Count);
        var targets = staged[2].Concat(staged[3]).Concat(staged[4]).Concat(staged[6]);
        if (!exactLimit)
        {
            targets = targets.Concat(staged[9]);
        }

        var planned = targets.ToArray();
        var source = Assert.Single(planned.Select(target => target.Address.Segment).Distinct());
        Assert.True(staged[7][0].Address.Segment > source);
        Assert.All(planned, target => CheckVacancies(node, target, planned));
        Assert.True(QueueOf(node).Capacity - QueueOf(node).Count > planned.Length + mediumSlots / 2 + smallSlots / 2);
        return (planned, staged[7], exactLimit ? [] : staged[10]);

        void StageClass(string type, int slotBytes, int regions, int[] live)
        {
            var byRegion = full.Where(location => location.Type == type).GroupBy(RegionOf)
                .OrderBy(group => group.Key).ToArray();
            Assert.Equal(regions, byRegion.Length);
            for (var region = 0; region < regions; region++)
            {
                var roots = byRegion[region].OrderBy(location => location.Address.Offset).ToArray();
                Assert.Equal(Arena.MaxBlockSize / slotBytes, roots.Length);
                Assert.All(roots, location => Assert.Equal(slotBytes, location.SlotBytes));
                staged.Add(roots.Take(live[region]).ToArray());
            }
        }
    }

    private static async Task EnableSourceAsync(EngineDatabase master, EngineDatabase node, Location[] source)
    {
        Assert.Equal(Arena.MaxBlockSize / (2 * source[0].SlotBytes), source.Length);
        Assert.All(source, location => Assert.Equal(location.Address, Find(node, location).Address));
        await DeleteAsync(master, source.Skip(1));
        await FollowAsync(master, node);
        Assert.Equal(source[0].Address, Find(node, source[0]).Address);
        foreach (var retired in source.Skip(1))
        {
            Assert.False(node.Set(retired.Type).TryGet(retired.Key, out _, out _));
            _ = retired.Arena.Header(retired.Address);
        }
    }

    private static Dictionary<ArenaAddress, byte[]> SnapshotEarlierVacancies(EngineDatabase node,
        CountingNativeAllocator allocator, Location candidate, Location[] retained) =>
        SnapshotEarlierVacancies(node, allocator, candidate.Address, candidate.SlotBytes,
            Locations(node).Concat(retained).Select(location => location.Address));

    internal static Dictionary<ArenaAddress, byte[]> SnapshotEarlierVacancies(EngineDatabase node,
        CountingNativeAllocator allocator, ArenaAddress candidate, int slotBytes, IEnumerable<ArenaAddress> liveWitnesses)
    {
        var witnesses = liveWitnesses.ToLookup(address => address.Segment);
        var vacancies = new Dictionary<ArenaAddress, byte[]>();
        Inspect(node, parts =>
        {
            var arena = parts.Arena;
            var sizeClass = SizeClasses.IndexFor(slotBytes - BlockHeader.Size);
            var candidateHeader = HeaderBytes(arena, candidate);
            for (var segmentIndex = 1u; segmentIndex < candidate.Segment; segmentIndex++)
            {
                if (ArenaSegmentTable.At(arena, segmentIndex) is not { } segment)
                {
                    continue;
                }

                nint native = 0;
                for (var region = 0; region < segment.Regions.Length; region++)
                {
                    ref var info = ref segment.Regions[region];
                    if (info.Allocated == 0 || info.SizeClass != sizeClass)
                    {
                        continue;
                    }

                    if (native == 0)
                    {
                        var witness = witnesses[segmentIndex].First();
                        var header = HeaderBytes(arena, witness);
                        var probe = new byte[BlockHeader.Size];
                        native = allocator.AddressOf(segment);
                        Marshal.Copy(native + (int)witness.Offset, probe, 0, probe.Length);
                        Assert.Equal(header, probe);
                    }

                    for (var slot = 0; slot < Arena.MaxBlockSize / slotBytes; slot++)
                    {
                        if (segment.IsAllocated(region, slot))
                        {
                            continue;
                        }

                        // Each vacancy once held a different real root. The actual native
                        // arena keeps its bytes until a write takes the slot again.
                        var offset = region * Arena.MaxBlockSize + slot * slotBytes;
                        var bytes = new byte[slotBytes];
                        Marshal.Copy(native + offset, bytes, 0, bytes.Length);
                        Assert.False(bytes.AsSpan(0, BlockHeader.Size).SequenceEqual(candidateHeader));
                        vacancies.Add(new ArenaAddress(segmentIndex, (uint)offset), bytes);
                    }
                }
            }
        });
        return vacancies;
    }

    private static byte[] HeaderBytes(Arena arena, ArenaAddress address) =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref arena.Header(address), 1)).ToArray();

    private static void CheckVacancies(EngineDatabase node, Location target, Location[] candidates)
    {
        var slots = Arena.MaxBlockSize / target.SlotBytes;
        var regionLoads = Locations(node).Where(location => location.SlotBytes == target.SlotBytes &&
            location.Address.Segment < target.Address.Segment).GroupBy(RegionOf);
        var genuinelyFree = regionLoads.Where(region => region.Count() >= slots / 2)
            .Sum(region => slots - region.Count());
        var needed = candidates.Count(candidate => candidate.SlotBytes == target.SlotBytes && Find(node, candidate).Address == candidate.Address);
        Assert.True(genuinelyFree >= Math.Max(needed, 1), "The earlier active regions cannot fit the remaining slots.");
        var (count, capacity) = QueueOf(node);
        Assert.True(capacity - count > 1, "An exhausted retirement queue could hide the workspace limit.");
    }

    private static void FillWorkspace(EngineDatabase node, Location[] candidates, int ordinaryBytes,
        CountingNativeAllocator allocator, int requests, int returns, int targetCharge, Action? checkTurn = null)
    {
        var moved = candidates.Count(target => Find(node, target).Address != target.Address);
        RunUntil(node, () => Charge(node) == targetCharge, () =>
        {
            checkTurn?.Invoke();
            var observed = CheckPinnedTurn(node, candidates, ordinaryBytes, allocator, requests, returns);
            Assert.InRange(observed, moved, moved + 1);
            moved = observed;
        });
    }

    private static int CheckPinnedTurn(EngineDatabase node, Location[] candidates, int ordinaryBytes,
        CountingNativeAllocator allocator, int requests, int returns)
    {
        var charged = Charge(node);
        var observed = candidates.Where(target => Find(node, target).Address != target.Address).ToArray();
        Assert.Equal(observed.Sum(location => location.SlotBytes), charged);
        Assert.InRange(charged, 0, WorkspaceLimit);
        Assert.Equal(ordinaryBytes + charged, node.Counters.Arena.RetiredBytes);
        Assert.Equal(0, node.Counters.Arena.PreparedBytes);
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns, allocator.Returned);
        Assert.Equal(allocator.Outstanding, node.Counters.Arena.Segments);
        foreach (var old in observed)
        {
            Assert.Equal(old.Handle, old.Arena.Header(old.Address).Root);
            DemandMoved(node, old);
        }

        return observed.Length;
    }

    private static void DemandMoved(EngineDatabase node, Location target, LatticeRevision? revision = null)
    {
        var current = Find(node, target);
        Assert.Equal(target.Handle, current.Handle);
        Assert.Equal(target.SlotBytes, current.SlotBytes);
        Assert.True(current.Address.Segment < target.Address.Segment);
        var observed = DemandRecord(node, target);
        if (revision.HasValue)
        {
            Assert.Equal(revision.Value, observed);
        }
    }

    private static void DemandFreed(Location target) =>
        Assert.Throws<ArgumentException>("address", () => _ = target.Arena.Header(target.Address));

    private static void DemandIndex(EngineDatabase node, Location target)
    {
        var items = node.Set(target.Type);
        var key = IndexKey.All.Range(IndexBound.Inclusive(CheckOf(target.Key)), IndexBound.Inclusive(CheckOf(target.Key)));
        var found = Assert.Single(items.Index("ByCheck").Find(key));
        Assert.Equal(target.Key, found.Get<long>("Id"));
    }

    private static ArenaAddress IndexRoot(EngineDatabase node, Location target)
    {
        var entityType = node.Set(target.Type).EntityType;
        var result = default(ArenaAddress);
        Assert.True(node.TryRunMaintenanceStep(parts =>
        {
            var tree = parts.Operations[entityType].Table.Indexes[0].Tree;
            var address = IndexRootAddress(tree);
            result = new ArenaAddress((uint)(address >> 32), (uint)address);
            Assert.Equal(BlockKindEnum.IndexNode, parts.Arena.Header(result).Kind);
            Assert.False(parts.Arena.IsPrepared(result));
        }));
        return result;
    }

    private static int Charge(EngineDatabase node)
    {
        var result = -1;
        Inspect(node, parts => result = parts.Retired.RelocationBytes);
        return result;
    }

    private static (int Count, int Capacity) QueueOf(EngineDatabase node)
    {
        var result = (Count: 0, Capacity: 0);
        Inspect(node, parts =>
        {
            var queue = RetiredItems(parts.Retired);
            result = (queue.Count, queue.EnsureCapacity(0));
        });
        return result;
    }

    private static void Inspect(EngineDatabase node, Action<DatabaseParts> inspect) =>
        Assert.True(SpinWait.SpinUntil(() => node.TryRunMaintenanceStep(inspect), Bound), "The writes lock did not become available.");

    private static void RunTurn(EngineDatabase node) =>
        Assert.True(SpinWait.SpinUntil(node.TryRunMaintenanceTurn, Bound), "No automatic turn was admitted.");

    private static void RunUntil(EngineDatabase node, Func<bool> reached, Action? checkTurn = null)
    {
        for (var turn = 0; turn < TurnBound && !reached(); turn++)
        {
            RunTurn(node);
            checkTurn?.Invoke();
        }

        Assert.True(reached(), "A small staged arena did not make the expected progress.");
    }

    private static void RunQuietTurns(EngineDatabase node, Action checkTurn)
    {
        for (var turn = 0; turn < TurnBound; turn++)
        {
            RunTurn(node);
            checkTurn();
        }
    }

    private static LatticeRevision DemandRecord(EngineDatabase node, Location target)
    {
        var items = node.Set(target.Type);
        Assert.True(items.TryGet(target.Key, out var record, out var revision));
        Assert.Equal(target.Key, record.Get<long>("Id"));
        Assert.Equal(CheckOf(target.Key), record.Get<long>("Check"));
        return revision;
    }

    private static Task HoldView(EngineDatabase node, Location target, LatticeRevision revision, out ViewerPause pause)
    {
        var viewer = new ViewerPause();
        pause = viewer;
        var items = node.Set(target.Type);
        return OwnThread.Run(() =>
        {
            Assert.True(items.TryView(target.Key, view =>
            {
                DemandView(ref view, target, revision);
                viewer.Hold();
                DemandView(ref view, target, revision);
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        });
    }

    private static void DemandView(ref LatticeRecordView view, Location target, LatticeRevision revision)
    {
        Assert.Equal(revision, view.Revision);
        Assert.Equal(target.Key, view["Id"].Get<long>());
        Assert.Equal(CheckOf(target.Key), view["Check"].Get<long>());
        Assert.True(Unsafe.AreSame(
            ref MemoryMarshal.GetReference(ViewRow(ref view).Bytes),
            ref MemoryMarshal.GetReference(target.Arena.Payload(target.Address))));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_row")]
    private static extern ref ViewRecord ViewRow(ref LatticeRecordView view);

    private static Location Find(EngineDatabase node, Location target) =>
        Assert.Single(Locations(node), location => location.Type == target.Type && location.Key == target.Key);

    private static List<Location> Locations(EngineDatabase node)
    {
        var roots = new List<Location>();
        node.ForEachLiveRoot((arena, layout, handle, address) =>
        {
            var payload = arena.Payload(address);
            var offset = layout.StoredFieldOf(layout.EntityType.Key).Offset - BlockHeader.Size;
            roots.Add(new Location(layout.EntityType.Name, MemoryMarshal.Read<long>(payload[offset..]), arena, handle,
                address, Arena.BlockSizeFor(payload.Length)));
        });
        return roots;
    }

    private static async Task InsertAsync(EngineDatabase master, string name, int first, int count)
    {
        var set = master.Set(name);
        var batch = master.CreateWriteBatch();
        for (var key = (long)first; key < first + count; key++)
        {
            batch.Insert(new LatticeRecord(set.EntityType) { ["Id"] = key, ["Check"] = CheckOf(key) });
        }

        await batch.CommitAsync().AsTask().WaitAsync(Bound);
    }

    private static async Task DeleteAsync(EngineDatabase master, IEnumerable<Location> locations)
    {
        var batch = master.CreateWriteBatch();
        foreach (var location in locations)
        {
            batch.Delete(location.Type, location.Key);
        }

        await batch.CommitAsync().AsTask().WaitAsync(Bound);
    }

    private static async Task FollowAsync(EngineDatabase master, EngineDatabase node)
    {
        if (node != master)
        {
            await node.WaitForPositionAsync(master.Position, Bound);
        }
    }

    private static EngineTestInputs Inputs(CountingNativeAllocator? allocator, int regions = 1, int reserve = 1,
        Action<MaintenanceTurnSample>? observe = null, int maxSegments = 32, Action<CancellationToken>? beforeWait = null) =>
        new()
        {
            Arena = new ArenaOptions { SegmentSize = regions * Arena.MaxBlockSize, MemoryLimit = (long)maxSegments * Arena.MaxBlockSize, ReservedSegments = reserve },
            Allocator = allocator,
            DisableBackgroundMaintenance = beforeWait is null,
            BeforeMaintenanceWait = beforeWait,
            ObserveMaintenanceTurn = observe,
        };

    private static LatticeSchema MakeSchema() => LatticeSchema.Compile($$"""
        {"StringEncoding":"Utf8","EntityTypes":[
          {{EntityType(1, "Big", 640)}},{{EntityType(2, "Medium", 400)}},{{EntityType(3, "Small", 244)}},
          {{EntityType(4, "Filler", 0)}},{{EntityType(5, "Indexed", 400, indexed: true)}}]}
        """);

    private static string EntityType(int id, string name, int nullableGuids, bool indexed = false)
    {
        var fields = Enumerable.Range(3, nullableGuids).Select(field =>
            $$""",{"Id":{{field}},"Name":"Pad{{field}}","Type":"Guid","Nullable":true}""");
        var index = indexed ? """, "Indexes":[{"Id":1,"Name":"ByCheck","Fields":[2]}]""" : "";
        return $$"""
            {"Id":{{id}},"Name":"{{name}}","Fields":[
              {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Check","Type":"Int64"}{{string.Concat(fields)}}
            ],"PrimaryKey":[1]{{index}}}
            """;
    }

    private static (uint Segment, uint Region) HeadOfClass(Location location)
    {
        var sizeClass = SizeClasses.IndexFor(location.SlotBytes - BlockHeader.Size);
        var head = ClassHeads(location.Arena)[sizeClass];
        return ((uint)(head >> 32), (uint)head);
    }

    private static (uint Segment, uint Region) RegionOf(Location location) =>
        (location.Address.Segment, location.Address.Offset / Arena.MaxBlockSize);

    private static long CheckOf(long key) => key * -11;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]
    private static extern ref Queue<RetiredItem> RetiredItems(RetiredMemory retired);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_regionsWithFreeSlot")]
    private static extern ref long[] ClassHeads(Arena arena);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_root")]
    private static extern ref ulong IndexRootAddress(OrderedTree tree);

    private sealed record Location(string Type, long Key, Arena Arena, RootHandle Handle, ArenaAddress Address, int SlotBytes);
}
