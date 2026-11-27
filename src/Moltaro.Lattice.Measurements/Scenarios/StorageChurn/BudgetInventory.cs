using System.Diagnostics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// The inventory of what an instance holds beside the charge of its budget (EngineDatabase.InspectHeld): the
// native grants the ledger counts with their segment objects, and the arrays the structures report, each
// sized here by the layout of a 64-bit runtime, grouped by the structure that reports them. Between two
// writes the charge is exactly that inventory and the headroom.
internal sealed class BudgetInventory
{
    // The object header and method table of a segment, and its five references and three counters.
    private static readonly long SegmentObjectBytes = Align(2L * IntPtr.Size + 5L * IntPtr.Size + 3 * sizeof(int));

    public Snapshot Read(string boundary, int frames, EngineDatabase database, ChurnLedger ledger, long limit)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var before = ledger.Snapshot();
            var held = database.InspectHeld();
            var after = ledger.Snapshot();
            if (before != after)
            {
                continue;
            }

            var native = (long)held.GrantedSegments * held.SegmentBytes;
            ChurnAudit.Require(after.Held == native, "Native grant ledger disagrees with the budget.");
            var owners = new SortedDictionary<string, Owner>(StringComparer.Ordinal);
            foreach (var array in held.Arrays)
            {
                var owner = owners.GetValueOrDefault(array.Owner);
                owners[array.Owner] = new Owner(owner.Objects + 1, owner.Bytes + BytesOf(array), 0);
            }

            var grants = native + held.GrantedSegments * SegmentObjectBytes;
            long independent = checked(grants + owners.Values.Sum(owner => owner.Bytes) + held.Headroom);
            ChurnAudit.Require(independent == held.Charged && owners.Count != 0,
                $"Independently sized charge {independent} disagrees with the budget {held.Charged}.");
            ChurnAudit.Require(held.Charged <= held.Peak && held.Peak <= limit,
                "Budget charge or peak exceeded the configured limit.");

            owners.Add("Native segment grants", new Owner(after.Allocations - after.Returns, grants, 0));
            owners.Add("Headroom", new Owner(1, held.Headroom, 0));
            foreach (var (name, owner) in owners.ToArray())
            {
                owners[name] = owner with { BudgetShare = (double)owner.Bytes / limit };
            }

            var gc = GC.GetGCMemoryInfo();
            var budget = new Figures(held.Charged, held.Peak, native, held.Headroom, held.DeliveryCapacityBytes);
            return new Snapshot(boundary, frames, Stopwatch.GetTimestamp(), independent, budget, after, owners,
                GC.GetTotalAllocatedBytes(precise: false), gc.HeapSizeBytes, gc.FragmentedBytes, gc.Index,
                GC.GetTotalPauseDuration().Ticks);
        }

        throw new InvalidOperationException("The independent inventory did not land between allocator events.");
    }

    // An array is its header of three words and its elements, rounded up to a word.
    public static long BytesOf(HeldArray array)
    {
        var element = array.ElementType.IsValueType ? RuntimeHelpers.SizeOf(array.ElementType.TypeHandle) : IntPtr.Size;
        return Align(3L * IntPtr.Size + (long)array.Length * element);
    }

    private static long Align(long bytes) => (bytes + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;

    internal readonly record struct Owner(long Objects, long Bytes, double BudgetShare);

    // The figures of the budget at the snapshot.
    internal readonly record struct Figures(long ChargedBytes, long PeakBytes, long NativeBytes, long HeadroomBytes,
        long DeliveryCapacityBytes);

    internal sealed record Snapshot(string Boundary, int Frames, long Ticks, long IndependentBytes,
        Figures Budget, ChurnLedger.Totals Native, IReadOnlyDictionary<string, Owner> Owners,
        long ProcessAllocatedBytes, long LastGcHeapBytes, long LastGcFragmentedBytes, long LastGcIndex,
        long ProcessGcPauseTimeSpanTicks);
}
