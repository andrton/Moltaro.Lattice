using System.Security.Cryptography;
using System.Text.Json;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Hashing;
using static Moltaro.Lattice.Measurements.Scenarios.StorageChurn.ManagedBackingSize;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// The growth and collision geometry of a primary table that a reader holds while the index grows. The arrays
// of the tables are discovered from the live table, independently of what the structures report.
internal static class ManagedPeakWitness
{
    public const string Name = "s24-managed-verify";
    private const long LooseLimit = 64L * LatticeDatabaseOptions.ArenaSegmentSize;
    private static readonly long[] Keys = [2244402025146343082, 482060900154538344, 5957141929804955818, 6799065134732835523,
        -4395606288385029375, -6266621041042679656, 1155341963689774562, 3497161724274112711, 9014700509047951766];

    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var schema = LatticeSchema.Compile("""
            {"EntityTypes":[{"Id":1,"Name":"Item","Fields":[{"Id":1,"Name":"Id","Type":"Int64"},
                {"Id":2,"Name":"Name","Type":"String"}],"PrimaryKey":[1]}]}
            """);
        var ledger = new ChurnLedger();
        var inventory = new BudgetInventory();
        var sizes = new ManagedBackingSize();
        BudgetInventory.Snapshot before;
        BudgetInventory.Snapshot overlap;
        BudgetInventory.Snapshot held;
        BudgetInventory.Snapshot released;
        long previousBytes;
        long narrow;
        long nextBytes;
        await using (var node = Create(schema, LooseLimit, ledger))
        {
            await node.OpenAsync(token);
            var set = node.Set("Item");
            await SeedAsync(set, token);
            var (index, queue) = HoldRetirementRoom(node);
            try
            {
                var original = index.TableAndVersion(out var version);
                var previous = Backings(original);
                previousBytes = previous.Sum(sizes.Of);
                before = inventory.Read("before-growth", 0, node, ledger, LooseLimit);

                using var leave = new ManualResetEventSlim();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                node.ArmReadSwitch(_ =>
                {
                    entered.TrySetResult();
                    ChurnAudit.Require(leave.Wait(TimeSpan.FromSeconds(30), token), "The held reader was not released.");
                });
                var reader = Task.Run(() => set.Get(Keys[0])!.Get<long>("Id"), token);
                BudgetInventory.Snapshot? switching = null;
                object[]? fresh = null;
                try
                {
                    await entered.Task.WaitAsync(token);
                    node.ArmGrowthSwitch(set.EntityType, () =>
                    {
                        var current = index.TableAndVersion(out var generation);
                        ChurnAudit.Require(generation == version + 1, "The new main table was not installed.");
                        fresh = Backings(current);
                        switching = inventory.Read("growth-old-and-new", 0, node, ledger, LooseLimit);
                    });

                    var changed = await set.InsertAsync(Row(set, Keys[8]), token);
                    ChurnAudit.Require(changed.Changed && fresh is not null && switching is not null, "The insertion did not reach primary growth.");
                    overlap = switching!;
                    nextBytes = fresh!.Sum(sizes.Of);
                    held = inventory.Read("after-growth-reader-held", 0, node, ledger, LooseLimit);
                    ChurnAudit.Require(before.Native == overlap.Native && before.Native == held.Native &&
                        overlap.Budget.PeakBytes >= before.IndependentBytes + nextBytes &&
                        node.Counters.RetiredIndexBytes > 0,
                        "The sample did not isolate managed overlap at an unchanged native grant.");
                }
                finally
                {
                    leave.Set();
                    ChurnAudit.Require(await reader.WaitAsync(token) == Keys[0], "The held reader missed the original row.");
                }

                node.DrainRetired();
                released = inventory.Read("reader-released", 0, node, ledger, LooseLimit);
                ChurnAudit.Require(held.IndependentBytes - released.IndependentBytes >= previousBytes &&
                    node.Counters.RetiredIndexBytes == 0, "The old-generation backing was not returned after release.");

                long net = Math.Max(before.IndependentBytes, overlap.IndependentBytes - previousBytes);
                narrow = net + (overlap.IndependentBytes - net) / 2;
                ChurnAudit.Require(released.IndependentBytes <= narrow && narrow < overlap.IndependentBytes,
                    "The budget did not distinguish the final shape from the old/new overlap.");
            }
            finally
            {
                ReleaseRetirementRoom(node);
            }
        }

        var narrowLedger = new ChurnLedger();
        BudgetInventory.Snapshot narrowBefore;
        BudgetInventory.Snapshot refused;
        BudgetInventory.Snapshot reused;
        await using (var node = Create(schema, narrow, narrowLedger))
        {
            await node.OpenAsync(token);
            var set = node.Set("Item");
            await SeedAsync(set, token);
            var (index, queue) = HoldRetirementRoom(node);
            try
            {
                var original = index.TableAndVersion(out var version);
                var old = Backings(original);
                var position = node.Position;
                narrowBefore = inventory.Read("narrow-before", 0, node, narrowLedger, narrow);
                long prepared = node.Counters.Arena.PreparedBytes;
                var grew = false;
                node.ArmGrowthSwitch(set.EntityType, () => grew = true);
                try
                {
                    await set.InsertAsync(Row(set, Keys[8]), token);
                    throw new InvalidOperationException("The narrow budget admitted overlapping primary tables.");
                }
                catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.WriteRejected &&
                    exception.Limit == LatticeLimitEnum.Memory && exception.OperationIndex is null)
                {
                    refused = inventory.Read("narrow-refused", 0, node, narrowLedger, narrow);
                    ChurnAudit.Require(!grew && ReferenceEquals(original, index.TableAndVersion(out var existing)) &&
                        version == existing && node.Position == position && !set.TryGet(Keys[8], out _) &&
                        narrowBefore.IndependentBytes == refused.IndependentBytes && narrowBefore.Native == refused.Native &&
                        node.Counters.Arena.PreparedBytes == prepared,
                        "Refused managed growth changed the installed generation, rooted capacity or position.");
                }

                ChurnAudit.Require((await set.DeleteAsync(Keys[7], token)).Changed, "The admitted removal did not create room.");
                node.DrainRetired();
                ChurnAudit.Require((await set.InsertAsync(Row(set, Keys[8]), token)).Changed && !grew,
                    "The refused insert did not reuse the same table after a peer key left.");
                reused = inventory.Read("narrow-reused", 0, node, narrowLedger, narrow);
                ChurnAudit.Require(reused.Native == narrowBefore.Native, "Recovery obtained a new native grant.");
            }
            finally
            {
                ReleaseRetirementRoom(node);
            }
        }

        ChurnAudit.Require(ledger.Snapshot().Held == 0 && narrowLedger.Snapshot().Held == 0, "The witness left arena grants behind.");
        string path = Path.Combine(".local", "measurements", $"s24-managed-witness-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss.fffffff}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var evidence = new
        {
            EngineSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(EngineDatabase).Assembly.Location))),
            PreviousBytes = previousBytes,
            ReplacementBytes = nextBytes,
            NarrowLimit = narrow,
            Before = before,
            Overlap = overlap,
            Held = held,
            Released = released,
            NarrowBefore = narrowBefore,
            Refused = refused,
            Reused = reused,
        };
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(evidence, new JsonSerializerOptions { WriteIndented = true }), token);
        return [$"Untimed primary-generation and managed rollback witness saved to {path}."];
    }

    private static EngineDatabase Create(LatticeSchema schema, long limit, ChurnLedger ledger) =>
        EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = schema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MemoryLimit = limit,
        }, new EngineTestInputs { Allocator = ledger, DisableBackgroundMaintenance = true, IndexSeed = 0 });

    private static async Task SeedAsync(ILatticeSet set, CancellationToken token)
    {
        foreach (long key in Keys[..8])
        {
            ChurnAudit.Require((await set.InsertAsync(Row(set, key), token)).Changed, "A collision key did not seed.");
        }
    }

    private static LatticeRecord Row(ILatticeSet set, long key) => new(set.EntityType) { ["Id"] = key, ["Name"] = "original" };

    private static (PrimaryIndex Index, Array Queue) HoldRetirementRoom(EngineDatabase node)
    {
        PrimaryIndex? index = null;
        Array? queue = null;
        ChurnAudit.Require(node.TryRunMaintenanceStep(parts =>
        {
            index = (PrimaryIndex)Field(parts.Tables.Stored(1), "_index")!;
            parts.Retired.Reserve(8);
            parts.Retired.Release(7);
            queue = (Array)Field(Field(parts.Retired, "_items")!, "_ring")!;
        }) && queue is { Length: >= 8 }, "Could not keep an independently observed retirement array.");
        return (index!, queue!);
    }

    private static void ReleaseRetirementRoom(EngineDatabase node) =>
        ChurnAudit.Require(node.TryRunMaintenanceStep(parts => parts.Retired.Release()), "Could not release the retained retirement place.");

    private static object[] Backings(IndexTable table)
    {
        var overflow = (OverflowBuckets)Field(table, "_overflow")!;
        var pages = (OverflowPage[])Field(overflow, "_pages")!;
        int used = (int)Field(overflow, "_pageCount")!;
        ChurnAudit.Require(used > 0 && table.Bytes > 0, "The collision fixture did not allocate overflow pages.");
        return [Field(table, "_words")!, pages, .. pages.Take(used).Select(page => page.Words)];
    }
}
