using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Text;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class ManagedGrowthRollbackTests
{
    [Fact]
    public async Task A_batch_refused_after_text_publication_workspace_and_primary_growth_reconciles_all_retained_backing()
    {
        const long limit = 8L * Segment;
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(limit, allocator, schema: IndexSchema.Utf8);
        var ranked = node.Set<IndexSchema.Ranked>();
        var uncounted = node.Set<IndexSchema.Uncounted>();
        for (var key = 1; key <= 8; key++)
        {
            await ranked.InsertAsync(Rank(key));
            await uncounted.InsertAsync(new IndexSchema.Uncounted { Id = key, Number = key });
        }

        node.DrainRetired();
        var parts = await MaintenanceStep.RunAfterAdmissionAsync(node, current => current);
        var primary = Backing<PrimaryIndex>(parts.Tables.Stored(2), "_index");
        var previous = primary.TableAndVersion(out var version);
        var other = Backing<PrimaryIndex>(parts.Tables.Stored(3), "_index");
        var otherBefore = other.TableAndVersion(out _);
        var initial = node.Position;
        var originalText = TextMetadataTestAccess.Capacity(node);
        var oldOwners = Owners();
        _ = ExpectCapacity(node, allocator, out var before, oldOwners);
        IDisposable? held = null;
        object[]? laterOwners = null;
        try
        {
            node.ArmGrowthSwitch(uncounted.EntityType, () =>
            {
                var grown = primary.TableAndVersion(out var newer);
                Assert.Equal(version + 1, newer);
                var currentText = TextMetadataTestAccess.Capacity(node);
                Assert.True(TextMetadataTestAccess.Bytes(currentText.WriterMetadata) >=
                    TextMetadataTestAccess.Bytes(originalText.WriterMetadata));
                Assert.True(node.Counters.TextEntries > 64);
                var publisher = Backing<Array>(parts.Publisher, "_tablesOf");
                Assert.True(publisher.Length >= 2);
                var grownOwners = Owners();
                laterOwners = grownOwners.Except(oldOwners, ReferenceEqualityComparer.Instance).ToArray();
                Assert.Contains(publisher, laterOwners);
                Assert.Contains(grown, laterOwners);
                Assert.Contains(laterOwners, owner => owner is uint[] or TextSlot[]);
                var during = ExpectCapacity(node, allocator, out _, grownOwners);
                Assert.True(during.ChargedBytes > before);
                held = node.HoldBudgetBacking(checked((int)(limit - during.ChargedBytes - 32)));
            });

            var batch = node.CreateWriteBatch();
            batch.Insert(new IndexSchema.Uncounted { Id = 9, Number = 9 });
            for (var key = 9; key <= 144; key++)
            {
                batch.Insert(Rank(key));
            }

            await RefusedAsync(batch.CommitAsync().AsTask(), LatticeLimitEnum.Memory);
            Assert.NotNull(laterOwners);
            Assert.Equal(initial, node.Position);
            Assert.Null(uncounted.Get(9L));
            Assert.Empty(ranked.Index("ByNumber").Find(IndexKey.Equal(9L)));
            Assert.Equal(8, ranked.Enumerate().Count());
            Assert.Equal(8, node.TextReferences().Count);
            Assert.Equal(16, node.Counters.RootEntries);
            OwnershipAudit.Expect(node);
        }
        finally
        {
            held?.Dispose();
        }

        node.DrainRetired();
        Assert.Same(previous, primary.TableAndVersion(out _));
        Assert.Same(otherBefore, other.TableAndVersion(out _));
        var rolledBack = Owners().ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.True(rolledBack.SetEquals(oldOwners), "Rollback changed the real holders of the original backing.");
        Assert.All(laterOwners!, added => Assert.DoesNotContain(added, rolledBack));
        Assert.Equal(oldOwners.Sum(ManagedObjectSize), rolledBack.Sum(ManagedObjectSize));
        _ = ExpectCapacity(node, allocator, out _, rolledBack.ToArray());
        _ = node.InspectBudget((_, backing) => Assert.DoesNotContain(backing, laterOwners!, ReferenceEqualityComparer.Instance));
        var admitted = await ranked.InsertAsync(Rank(9)).AsTask().WaitAsync(Bound);
        Assert.Equal(admitted.Position, node.Position);
        Assert.Equal(9L, Assert.Single(ranked.Index("ByNumber").Find(IndexKey.Equal(9L))).Id);

        object[] Owners() => TextMetadataTestAccess.UnderWrites<object[]>(node, current =>
        {
            var text = current.Arena.Texts;
            var active = Backing<TextBucketSet>(text, "_active");
            var slots = Backing<TextWriterSlots>(text, "_slots");
            var writer = Enumerable.Range(0, Backing<int>(slots, "_pageCount"))
                .Select(page => text.CaptureWriterPage((uint)page)).OfType<object>();
            var scratch = new[] { "_tablesOf", "_claims", "_freed" }
                .Select(field => Backing<Array>(current.Publisher, field)).Where(array => array.Length > 0);
            var old = Backing<TextBucketSet?>(text, "_old");
            return [.. scratch, .. writer, .. TextMetadataShapeTests.BucketObjects(active),
                .. (old is null ? [] : TextMetadataShapeTests.BucketObjects(old)),
                .. PrimaryGenerationBudgetTests.ArraysOf(primary.TableAndVersion(out _)),
                .. PrimaryGenerationBudgetTests.ArraysOf(other.TableAndVersion(out _))];
        });
    }

    private static IndexSchema.Ranked Rank(int key) =>
        new() { Id = key, Number = key, Login = $"login {key}", Region = $"region {key}" };
}
