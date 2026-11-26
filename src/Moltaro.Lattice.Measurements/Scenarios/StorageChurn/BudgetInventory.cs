using System.Diagnostics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal sealed class BudgetInventory
{
    private readonly ManagedBackingSize _sizes = new();

    public Snapshot Read(string boundary, int frames, EngineDatabase database, ChurnLedger ledger, long limit,
        object[]? present = null, object[]? absent = null)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var before = ledger.Snapshot();
            var owners = new SortedDictionary<string, Owner>(StringComparer.Ordinal);
            var visited = new ConditionalWeakTable<object, object>();
            var sample = database.InspectBudget((holder, backing) =>
            {
                ChurnAudit.Require(visited.TryAdd(backing, this), $"Duplicate backing reported for {holder}.");
                var owner = owners.GetValueOrDefault(holder);
                owners[holder] = new Owner(owner.Objects + 1, owner.Bytes + _sizes.Of(backing), 0);
            });
            var after = ledger.Snapshot();
            if (before != after)
            {
                continue;
            }

            ChurnAudit.Require(present is null || present.All(item => visited.TryGetValue(item, out _)),
                "The budget inventory omitted a directly discovered backing.");
            ChurnAudit.Require(absent is null || absent.All(item => !visited.TryGetValue(item, out _)),
                "The budget inventory still contains a released backing.");
            ChurnAudit.Require(after.Held == sample.NativeBytes, "Native grant ledger disagrees with the budget.");
            long independent = checked(after.Held + owners.Values.Sum(owner => owner.Bytes));
            ChurnAudit.Require(independent == sample.ChargedBytes && owners.Count != 0,
                $"Independently sized charge {independent} disagrees with the budget {sample.ChargedBytes}.");
            ChurnAudit.Require(sample.ChargedBytes <= sample.PeakBytes && sample.PeakBytes <= limit,
                "Budget charge or peak exceeded the configured limit.");

            owners.Add("Native segment grants", new Owner(after.Allocations - after.Returns, after.Held, 0));
            foreach (var (name, owner) in owners.ToArray())
            {
                owners[name] = owner with { BudgetShare = (double)owner.Bytes / limit };
            }

            var gc = GC.GetGCMemoryInfo();
            return new Snapshot(boundary, frames, Stopwatch.GetTimestamp(), independent, sample, after, owners,
                GC.GetTotalAllocatedBytes(precise: false), gc.HeapSizeBytes, gc.FragmentedBytes, gc.Index,
                GC.GetTotalPauseDuration().Ticks);
        }

        throw new InvalidOperationException("The independent inventory did not land between allocator events.");
    }

    internal readonly record struct Owner(long Objects, long Bytes, double BudgetShare);

    internal sealed record Snapshot(string Boundary, int Frames, long Ticks, long IndependentBytes,
        BudgetCapacitySample Budget, ChurnLedger.Totals Native, IReadOnlyDictionary<string, Owner> Owners,
        long ProcessAllocatedBytes, long LastGcHeapBytes, long LastGcFragmentedBytes, long LastGcIndex,
        long ProcessGcPauseTimeSpanTicks);
}
