using System.Reflection;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Hashing;
using static Moltaro.Lattice.Measurements.Scenarios.StorageChurn.ManagedBackingSize;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Uses the same physical inspection on both engines, without their budget totals.
internal sealed class StandingReserveInventory
{
    private const string RootMetadata = "Root table metadata";
    private const string RootPages = "Root page directory and pages";
    private const string PrimaryTables = "Primary index tables";
    private const string OrderedArrays = "Ordered index working arrays";
    private const string RetirementQueue = "Retirement queue";
    private const string PublicationWorkspace = "Publication workspace";
    private const string CommitGroup = "Commit group";
    private const string AllocatorMetadata = "Allocator metadata";
    private const string TextMetadata = "Text metadata";
    private readonly ManagedBackingSize _sizes = new();

    public Snapshot Read(EngineDatabase node, ChurnLedger ledger)
    {
        Snapshot? result = null;
        bool entered = node.TryRunMaintenanceStep(parts =>
        {
            var arena = parts.Arena;
            var segments = (ArenaSegment?[])Field(arena, "_segments")!;
            var index = parts.Tables.InSchemaOrder.Single(table => table.EntityType.Name == "Indexed").Indexes[0];
            var pool = (NodePool)Field(index.Tree, "_pool")!;
            var reserved = new List<ulong>();
            var address = (ArenaAddress)Field(pool, "_head")!;
            while (!address.IsNull)
            {
                reserved.Add(address.Value);
                address = MemoryMarshal.Read<ArenaAddress>(arena.Payload(address));
            }

            ChurnAudit.Require(reserved.Count == index.Tree.ReservedNodes, "The reserved nodes did not reconcile.");
            var shape = new SortedDictionary<string, Backing>(StringComparer.Ordinal);
            var managed = new SortedDictionary<string, long>(StringComparer.Ordinal);
            void Record(string category, string field, object? owner, bool includesEmpty = false)
            {
                int? length = (owner as Array)?.Length;
                long bytes = owner is null ? 0 : _sizes.Of(owner);
                long accounted = length == 0 && !includesEmpty ? 0 : bytes;
                string? type = owner is null ? null : ConcreteType(owner.GetType());
                shape.Add($"{category}/{field}", new Backing(category, field, type, owner is null ? 0 : 1, length, bytes, accounted));
                managed[category] = managed.GetValueOrDefault(category) + accounted;
            }

            var roots = parts.Roots;
            Record(RootMetadata, "Roots", roots);
            var directoryField = roots.GetType().GetField("_smallerDirectories", BindingFlags.Instance | BindingFlags.NonPublic);
            var smallerDirectories = (Array?)directoryField?.GetValue(roots);
            Record(RootMetadata, "Roots._smallerDirectories", smallerDirectories);
            if (directoryField is not null)
            {
                ChurnAudit.Require(smallerDirectories is not null && (int)Field(roots, "_smallerDirectoryCount")! == 0 &&
                    smallerDirectories.Cast<object?>().All(directory => directory is null) &&
                    Field(roots, "_generations") is null && (uint)Field(roots, "_generation")! == 0,
                    "The reserve sample still retains a previous root directory or separate generation history.");
            }

            var rootPages = (Array)Field(roots, "_pages")!;
            Record(RootPages, "_pages", rootPages);
            for (int page = 0; page < rootPages.Length; page++)
            {
                Record(RootPages, $"_pages[{page}]", rootPages.GetValue(page));
            }

            foreach (var entity in parts.Tables.InSchemaOrder)
            {
                string tableField = $"{entity.EntityType.Name}._index._table";
                var primary = Field(entity, "_index")!;
                var table = Field(primary, "_table")!;
                var overflow = Field(table, "_overflow")!;
                ChurnAudit.Require(AddedField(primary, "_previous") is null && AddedField(overflow, "_smaller") is null,
                    "A quiescent reserve sample still has earlier primary backing.");
                Record(PrimaryTables, tableField, table);
                Record(PrimaryTables, $"{tableField}._words", Field(table, "_words"));
                Record(PrimaryTables, $"{tableField}._overflow", overflow);
                var pages = (OverflowPage[])Field(overflow, "_pages")!;
                Record(PrimaryTables, $"{tableField}._overflow._pages", pages, includesEmpty: true);
                for (int page = 0; page < pages.Length; page++)
                {
                    Record(PrimaryTables, $"{tableField}._overflow._pages[{page}].Words", pages[page].Words, includesEmpty: true);
                }
            }

            Record(OrderedArrays, "Index._entry", Field(index, "_entry"), includesEmpty: true);
            foreach (string field in new[] { "_path", "_slots", "_replaced" })
            {
                Record(OrderedArrays, $"Tree.{field}", Field(index.Tree, field));
            }

            foreach (string field in new[] { "_progress", "_baseProgress" })
            {
                Record(OrderedArrays, $"Pool.{field}", Field(pool, field));
            }

            Record(RetirementQueue, "_items._array", Field(Field(parts.Retired, "_items")!, "_array"));
            foreach (string field in new[] { "_tablesOf", "_claims", "_freed" })
            {
                Record(PublicationWorkspace, field, Field(parts.Publisher, field));
            }

            foreach (string field in new[] { "_tableWorkspace", "_claimWorkspace", "_freedWorkspace" })
            {
                var current = AddedField(parts.Publisher, field);
                ChurnAudit.Require(current is null || AddedField(current, "_earlier") is null,
                    "A quiescent reserve sample still has an earlier publication workspace.");
            }

            Record(CommitGroup, "_group._writes._items", Field(Field(Field(parts.Coordinator, "_group")!, "_writes")!, "_items"));
            Record(AllocatorMetadata, "_segments", segments, includesEmpty: true);
            Record(AllocatorMetadata, "_regionsWithFreeSlot", Field(arena, "_regionsWithFreeSlot"));
            ChurnAudit.Require(AddedField(arena, "_smallerDirectory") is null, "A quiescent reserve sample still has an earlier arena directory.");
            Record(AllocatorMetadata, "_freedSegmentSlots", SegmentDirectory(Field(arena, "_freedSegmentSlots")!));
            Record(AllocatorMetadata, "_reservedSegments", SegmentDirectory(Field(arena, "_reservedSegments")!));
            for (int slot = 0; slot < segments.Length; slot++)
            {
                if (segments[slot] is { } segment)
                {
                    string segmentField = $"_segments[{slot}]";
                    Record(AllocatorMetadata, segmentField, segment);
                    Record(AllocatorMetadata, $"{segmentField}.Regions", segment.Regions, includesEmpty: true);
                    foreach (string field in new[] { "_slotBits", "_preparedBits", "_retiredBits" })
                    {
                        Record(AllocatorMetadata, $"{segmentField}.{field}", Field(segment, field));
                    }
                }
            }

            var texts = arena.Texts;
            Record(TextMetadata, "Texts", texts);
            ChurnAudit.Require(Field(texts, "_old") is null, "A quiescent reserve sample still has an old text bucket generation.");
            foreach (string name in new[] { "_locator", "_slots", "_active" })
            {
                var owner = Field(texts, name)!;
                string field = $"Texts.{name}";
                Record(TextMetadata, field, owner);
                var pages = Field(owner, "_pages")!;
                Record(TextMetadata, $"{field}._pages", pages);
                ChurnAudit.Require((int)Field(pages, "_branches")! == 0, "The reserve sample did not have a direct text page.");
                var page = Field(pages, "_root");
                string pageField = $"{field}._pages._root";
                Record(TextMetadata, pageField, page);
                if (name == "_locator" && page is not null)
                {
                    ChurnAudit.Require(AddedField(page, "Earlier") is null, "A quiescent reserve sample still has earlier locator backing.");
                    Record(TextMetadata, $"{pageField}.Addresses", Field(page, "Addresses"));
                    Record(TextMetadata, $"{pageField}.Ordinals", Field(page, "Ordinals"));
                }
                else if (name == "_active" && page is not null)
                {
                    Record(TextMetadata, $"{pageField}.Heads", Field(page, "Heads"));
                }
            }

            ChurnAudit.Require(AddedField(Field(texts, "_slots")!, "_earlierFirst") is null,
                "A quiescent reserve sample still has earlier text writer backing.");

            var textCapacity = texts.MeasureCapacity();
            long previousTextBytes = new[] { textCapacity.LocatorDirectory, textCapacity.LocatorLeaves, textCapacity.WriterMetadata,
                textCapacity.ActiveBuckets, textCapacity.OldBuckets, textCapacity.RetiredLocator }.SelectMany(group => group)
                .Sum(item => item.InstanceCount * Align(item.ArrayElementBytes == 0
                    ? 2L * IntPtr.Size + item.ObjectFieldBytes
                    : 3L * IntPtr.Size + (long)item.ArrayElementBytes * item.ArrayLength));
            ChurnAudit.Require(managed[TextMetadata] == previousTextBytes, "The physical text fields did not reconcile with the raw text census.");

            var regions = segments.OfType<ArenaSegment>().SelectMany(segment => segment.Regions)
                .Select(region => new Region(region.Allocated == 0 ? 0 : SizeClasses.SizeOf(region.SizeClass),
                    region.Allocated, region.Live)).OrderBy(region => region.SlotBytes).ToArray();
            var grants = ledger.Snapshot();
            ChurnAudit.Require(grants.Held == (long)regions.Length * Arena.MaxBlockSize, "The actual one-region grants did not reconcile.");
            result = new Snapshot(grants, arena.Counters, node.Counters.MemoryLimit, parts.Roots.Count, index.Tree.Nodes().Count,
                (long)Field(pool, "_reserve")!, reserved.ToArray(), regions, managed, shape);
        });

        ChurnAudit.Require(entered && result is not null, "The reserve fixture was not quiescent.");
        return result!;
    }

    private static int[] SegmentDirectory(object directory) => directory switch
    {
        int[] slots => slots,
        Stack<int> stack => (int[])Field(stack, "_array")!,
        _ => throw new InvalidOperationException("The arena segment directory uses an unknown backing."),
    };

    private static string ConcreteType(Type type)
    {
        if (type.IsArray)
        {
            return $"{ConcreteType(type.GetElementType()!)}[]";
        }

        return type.IsGenericType
            ? $"{type.GetGenericTypeDefinition().FullName}[{string.Join(",", type.GetGenericArguments().Select(ConcreteType))}]"
            : type.FullName!;
    }

    private static long Align(long bytes) => (bytes + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;

    // The older engine has no field for these rollback owners.
    private static object? AddedField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(owner);

    internal sealed record Snapshot(ChurnLedger.Totals Native, ArenaCounters Arena, long EffectiveLimit, int RootCount, int LiveIndexNodes,
        long StandingTarget, ulong[] Pool, Region[] Regions, IReadOnlyDictionary<string, long> ManagedBackingBytes,
        IReadOnlyDictionary<string, Backing> ManagedShape);

    internal readonly record struct Backing(string Category, string Field, string? ConcreteType, int Count, int? Length,
        long ActualBytes, long AccountedBytes);

    internal readonly record struct Region(int SlotBytes, ushort Allocated, ushort Live);
}
