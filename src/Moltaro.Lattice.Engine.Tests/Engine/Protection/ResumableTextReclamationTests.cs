using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Text;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.ResumableReclamationTests;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Text entries belong to every physical descriptor and secondary index key that names them.
// A bounded release can yield before unlink, or with unlinked storage still counted in the
// arena (docs/design/arena-protocol.md, "Deferred reclamation", and ordered-index.md).
public sealed class ResumableTextReclamationTests
{
    private const ulong IndexSeed = 18_041;
    private const int Collisions = 29;

    private static readonly LatticeSchema Schema = ItemSchema.Compile("""
        { 'Id': 2, 'Name': 'Text', 'Type': 'String', 'StringStorage': 'Dictionary' }
        """);

    [Theory(Skip = ResumableDrainNotImplemented)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_paused_final_release_observes_a_new_owner_or_restarts_after_table_growth(bool addOwner)
    {
        await using var reference = await EngineMaster.OpenAsync(Schema, IndexSeed);
        var expected = await StageCollisionChainAsync(reference);
        var cost = UntilUnlinked(reference, expected);
        Finish(reference);

        // Equal one-descriptor roots and the same table shape. Only how far the
        // release searches differs, so each additional bucket must cost a turn.
        await using var shallow = await EngineMaster.OpenAsync(Schema, IndexSeed);
        var early = await StageCollisionChainAsync(shallow, targetNumber: 1);
        Assert.Equal(1, early.Buckets);
        var earlyCost = UntilUnlinked(shallow, early);
        Assert.True(cost >= earlyCost + expected.Buckets - early.Buckets);
        Finish(shallow);

        await using var database = await EngineMaster.OpenAsync(Schema, IndexSeed);
        var target = await StageCollisionChainAsync(database);
        Assert.Equal((expected.Buckets, expected.Text, expected.Version), (target.Buckets, target.Text, target.Version));

        for (var turn = 1; turn < cost; turn++)
        {
            Assert.Equal(1, Step(database, 1));
            Assert.Equal(target.Buckets, ObserveBucket(database, target).Distance);
            Assert.Equal(1L, References(database, target.Text));
        }

        var items = database.Set("Item");
        const long OtherOwner = 10_000;
        var colliderTexts = Enumerable.Range(1, Collisions - 1).Select(index =>
            (Key: (long)index, Text: items.Get((long)index)!.Get<string>("Text")!)).ToArray();

        if (addOwner)
        {
            await InsertAsync(items, OtherOwner, target.Text);
            Assert.Equal(target.Entry, EntryOf(database, OtherOwner));
            Assert.Equal(2L, References(database, target.Text));
            Assert.Equal(target.Version, ObserveBucket(database, target).Version);

            Finish(database);
            Assert.Equal(target.Entry, EntryOf(database, OtherOwner));
            Assert.Equal(1L, References(database, target.Text));
            Assert.Equal(target.Text, items.Get(OtherOwner)!.Get<string>("Text"));
        }
        else
        {
            // Real inserts force the exact active table to change. Starting near its growth
            // boundary keeps automatic publication cleanup from taking over the partial item.
            var inserted = new List<(long Key, string Text)>();
            var beforeGrowth = ObserveBucket(database, target);
            for (var index = 0; ObserveBucket(database, target).Version == beforeGrowth.Version; index++)
            {
                Assert.True(index < 8, "The staged table was not near growth.");
                var key = OtherOwner + index;
                var text = "growth-" + index.ToString(CultureInfo.InvariantCulture);
                await InsertAsync(items, key, text);
                inserted.Add((key, text));
            }

            var afterGrowth = ObserveBucket(database, target);
            Assert.True(afterGrowth.Version > beforeGrowth.Version);
            Assert.NotEqual(beforeGrowth.Home, afterGrowth.Home);
            Assert.NotNull(afterGrowth.Distance);
            Assert.Equal(1L, References(database, target.Text));

            Finish(database);
            Assert.Null(ObserveBucket(database, target).Distance);
            Assert.Equal(0L, References(database, target.Text));
            Assert.All(inserted, entry => Assert.Equal(entry.Text, items.Get(entry.Key)!.Get<string>("Text")));
        }

        Assert.All(colliderTexts, entry => Assert.Equal(entry.Text, items.Get(entry.Key)!.Get<string>("Text")));
    }

    [Theory(Skip = ResumableDrainNotImplemented)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlinked_text_keeps_its_id_and_native_storage_until_its_last_chunk_is_returned(bool finishWithOrdinaryDrain)
    {
        var allocator = new CountingNativeAllocator();
        var options = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 32L * Arena.MaxBlockSize, ReservedSegments = 0 };
        await using var database = await EngineMaster.OpenAsync(Schema, new EngineTestInputs
        {
            Arena = options,
            Allocator = allocator,
            IndexSeed = IndexSeed,
        });
        var items = database.Set("Item");
        var text = new string('t', ValueBlocks.ChunkPayload * 2 + 17);
        await InsertAsync(items, 1, text);
        var oldRoot = RootOf(database, 1);
        var entry = EntryOf(database, 1);
        var chunks = TextBlocks(entry).ToArray();
        Assert.Equal(4, chunks.Length);
        var old = new Target(text, entry, 0, 0);
        var originalBytes = chunks.Sum(address => SlotBytes(entry.Arena, address)) + SlotBytes(entry.Arena, oldRoot);
        Assert.Equal(originalBytes, CountedSlots(database));

        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(1L, view =>
            {
                pause.Hold();
                return view["Text"].ByteLength;
            }, out var length));
            return length;
        });
        await pause.EnteredAsync(reading);

        await items.DeleteAsync(1L);
        Assert.Equal(0, Step(database, 1));
        pause.Release();
        Assert.Equal(text.Length, await reading.WaitAsync(Bound));
        var returned = allocator.Returned;

        UntilUnlinked(database, old);
        Assert.All(chunks, address => Assert.True(IsAllocated(entry.Arena, address)));
        Assert.True(IsAllocated(entry.Arena, oldRoot));
        Assert.Equal(originalBytes, CountedSlots(database));
        Assert.Equal(returned, allocator.Returned);

        await InsertAsync(items, 2, text);
        var replacement = EntryOf(database, 2);
        Assert.NotEqual(entry.Id, replacement.Id);
        Assert.NotEqual(entry.Address, replacement.Address);
        Assert.Equal(text, items.Get(2L)!.Get<string>("Text"));

        var replacementBytes = TextBlocks(replacement).Sum(address => SlotBytes(replacement.Arena, address)) +
            SlotBytes(replacement.Arena, RootOf(database, 2));
        var remaining = chunks.Length;
        var turns = 0;
        var slotSizes = chunks.ToDictionary(address => address, address => SlotBytes(entry.Arena, address));
        var oldRootBytes = SlotBytes(entry.Arena, oldRoot);
        while (remaining != 0)
        {
            Assert.True(turns++ < 128, "An unlinked text did not release its chunks.");
            Assert.Equal(1, Step(database, 1));
            var held = chunks.Where(address => IsAllocated(entry.Arena, address)).ToArray();
            Assert.InRange(remaining - held.Length, 0, 1);
            remaining = held.Length;
            if (remaining > 0)
            {
                Assert.Contains(entry.Address, held);
            }

            Assert.Equal(held.Sum(address => slotSizes[address]) +
                (IsAllocated(entry.Arena, oldRoot) ? oldRootBytes : 0) + replacementBytes, CountedSlots(database));
            Assert.Equal(allocator.Outstanding, database.Counters.Arena.Segments);
            if (finishWithOrdinaryDrain && remaining == chunks.Length - 1)
            {
                database.DrainRetired();
                break;
            }
        }

        Finish(database);
        Assert.All(chunks, address => Assert.False(IsAllocated(entry.Arena, address)));
        Assert.False(IsAllocated(entry.Arena, oldRoot));
        Assert.Equal(replacementBytes, CountedSlots(database));
        Assert.True(allocator.Returned >= returned + 2);

        await InsertAsync(items, 3, "another dictionary text");
        Assert.Equal(entry.Id, EntryOf(database, 3).Id);
        Assert.Equal(replacement, EntryOf(database, 2));
        Assert.Equal(1L, References(database, text));
    }

    [Fact(Skip = ResumableDrainNotImplemented)]
    public async Task A_remaining_index_separator_owns_its_text_after_the_last_entity_is_reclaimed()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var ranked = database.Set<IndexSchema.Ranked>();
        for (var id = 1L; id <= 600; id++)
        {
            await ranked.InsertAsync(new IndexSchema.Ranked
            {
                Id = id,
                Number = id,
                Login = new string('x', 80) + id.ToString("D6", CultureInfo.InvariantCulture),
                Region = "",
            });
        }

        database.DrainRetired();
        var index = ranked.Index("ByLogin");
        string? separator = null;
        var tree = IndexContents.Of(database, "Ranked", "ByLogin");
        Assert.True(tree.Height >= 2);
        // If a candidate merges its leaf, try the next actual separator. Require a
        // survivor rather than inferring one from a count of input keys.
        foreach (var candidate in tree.RootSeparators.Select(key => (string)key[0]!).Reverse())
        {
            var root = Assert.Single(index.Find(IndexKey.Equal(candidate)));
            await ranked.DeleteAsync(root.Id);
            Assert.True(database.Counters.Arena.RetiredBytes > 0);
            Finish(database);
            var after = IndexContents.Of(database, "Ranked", "ByLogin");
            if (after.NodeKeys.Any(node => node.Level > 0 && node.Keys.Any(key => Equals(key[0], candidate))))
            {
                separator = candidate;
                break;
            }
        }

        Assert.NotNull(separator);
        Assert.Empty(index.Find(IndexKey.Equal(separator)));
        var kept = IndexContents.Of(database, "Ranked", "ByLogin");
        var owners = kept.NodeKeys.Where(node => node.Level > 0)
            .Sum(node => node.Keys.Count(key => Equals(key[0], separator)));
        Assert.True(owners > 0);
        Assert.Equal(owners, References(database, separator));

        var before = kept.Entries.Last(entry => string.CompareOrdinal((string)entry.Key[0]!, separator) < 0);
        var afterSeparator = kept.Entries.First(entry => string.CompareOrdinal((string)entry.Key[0]!, separator) > 0);
        Assert.Equal(new[] { before.Entity, afterSeparator.Entity }, index.Find(IndexKey.All.Range(
            IndexBound.Inclusive((string)before.Key[0]!), IndexBound.Inclusive((string)afterSeparator.Key[0]!))).Select(item => item.Id));

        foreach (var entry in kept.Entries)
        {
            await ranked.DeleteAsync(entry.Entity);
        }

        Finish(database);
        Assert.Equal(0L, References(database, separator));
    }

    private static async Task<Target> StageCollisionChainAsync(EngineDatabase database, int targetNumber = Collisions)
    {
        var items = database.Set("Item");
        // Pre-grow the active table, then build a chain with enough colliding live
        // entries to put the target multiple buckets from its main bucket.
        var seeded = 0;
        while (TableShape(database).Bits < 5)
        {
            Assert.True(seeded < 256);
            seeded++;
            await InsertAsync(items, -seeded, "pad-" + seeded.ToString(CultureInfo.InvariantCulture));
        }

        for (var key = -1L; key >= -seeded; key--)
        {
            await items.DeleteAsync(key);
        }

        database.DrainRetired();
        Assert.Equal(0, database.Counters.TextEntries);
        var initial = TableShape(database);
        var bucketShape = new IndexTable(initial.Bits, initial.Version);
        var grownShape = new IndexTable(initial.Bits + 1, initial.Version + 1);
        var selected = 0;
        for (var index = 0; selected < Collisions; index++)
        {
            Assert.True(index < 32_000, "Could not stage enough same-home text hashes.");
            var text = "collider-" + index.ToString(CultureInfo.InvariantCulture);
            var hash = HashOf(text);
            if (bucketShape.BucketOf(hash) == 0 &&
                (selected != Collisions - 1 || grownShape.BucketOf(hash) == 1))
            {
                await InsertAsync(items, ++selected, text);
            }
        }

        var padding = 0;
        while (!TableShape(database).Full)
        {
            Assert.True(padding < 256);
            padding++;
            var key = -seeded - padding;
            await InsertAsync(items, key, "fill-" + padding.ToString(CultureInfo.InvariantCulture));
        }

        database.DrainRetired();
        Assert.Equal(initial.Version, TableShape(database).Version);
        var value = items.Get((long)targetNumber)!.Get<string>("Text")!;
        var entry = EntryOf(database, targetNumber);
        var target = new Target(value, entry, initial.Version, 0);
        var observed = ObserveBucket(database, target);
        Assert.True(observed.Distance >= (targetNumber == 1 ? 1 : 4), "The target did not go into its expected part of the chain.");
        await items.DeleteAsync((long)targetNumber);
        Assert.Equal(1L, References(database, value));
        return target with { Buckets = observed.Distance!.Value };
    }

    private static (int Bits, int Version, bool Full) TableShape(EngineDatabase database)
    {
        var result = (Bits: 0, Version: 0, Full: false);
        Assert.True(database.TryRunMaintenanceStep(parts =>
        {
            var index = Buckets(parts.Arena.Texts);
            result = (index.BucketBits, index.Version, index.IsFull(0));
        }));
        return result;
    }

    private static (int Version, int Home, int? Distance) ObserveBucket(EngineDatabase database, Target target)
    {
        (int Version, int Home, int? Distance) result = default;
        Assert.True(database.TryRunMaintenanceStep(parts =>
        {
            var index = Buckets(parts.Arena.Texts);
            var hash = HashOf(target.Text);
            var main = index.BucketOf(hash);
            var wanted = IndexTable.EntryOf(hash, target.Entry.Id);
            var overflow = 0L;
            var distance = 0;
            do
            {
                distance++;
                for (var word = 0; word < IndexTable.LinkWord; word++)
                {
                    var entry = overflow == 0 ? index.ReadMain(main, word) : index.ReadOverflow(overflow, word);
                    if (entry == wanted)
                    {
                        result = (index.Version, main, distance);
                        return;
                    }
                }

                overflow = overflow == 0
                    ? index.ReadMain(main, IndexTable.LinkWord)
                    : index.ReadOverflow(overflow, IndexTable.LinkWord);
            } while (overflow != 0);

            result = (index.Version, main, null);
        }));
        return result;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_buckets")]
    private static extern ref IndexTable Buckets(TextTable table);

    private static uint HashOf(string text)
    {
        var hash = new Marvin(IndexSeed);
        hash.Append(Encoding.UTF8.GetBytes(text));
        return hash.Finish();
    }

    private static int UntilUnlinked(EngineDatabase database, Target target)
    {
        var turns = 0;
        while (ObserveBucket(database, target).Distance.HasValue)
        {
            Assert.True(turns++ < 512, "The final text reference did not unlink.");
            Assert.Equal(1, Step(database, 1));
        }

        Assert.True(turns > 0);
        return turns;
    }

    private static void Finish(EngineDatabase database)
    {
        for (var turn = 0; turn < 1024; turn++)
        {
            var work = Step(database, 64);
            Assert.InRange(work, 0, 64);
            if (work == 0)
            {
                Assert.Equal(0, database.Counters.Arena.RetiredBytes);
                return;
            }
        }

        Assert.Fail("Eligible retirement work did not finish.");
    }

    private static Task InsertAsync(ILatticeSet items, long key, string value) =>
        items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = key, ["Text"] = value }).AsTask();

    private static long References(EngineDatabase database, string value)
    {
        var matches = database.TextReferences().Where(entry => Encoding.UTF8.GetString(entry.Text) == value).ToArray();
        return matches.Length == 0 ? 0 : Assert.Single(matches).References;
    }

    private static (Arena Arena, RootLayout Layout, ArenaAddress Address) LocationOf(EngineDatabase database, long key)
    {
        (Arena Arena, RootLayout Layout, ArenaAddress Address) result = default;
        database.ForEachLiveRoot((arena, layout, _, address) =>
        {
            if (new RootReader(arena, layout, address).GetInteger(layout.EntityType.Key) == key)
            {
                result = (arena, layout, address);
            }
        });
        Assert.False(result.Address.IsNull);
        return result;
    }

    private static ArenaAddress RootOf(EngineDatabase database, long key) => LocationOf(database, key).Address;

    private static TextEntry EntryOf(EngineDatabase database, long key)
    {
        var (arena, layout, address) = LocationOf(database, key);
        var reader = new RootReader(arena, layout, address);
        var descriptor = reader.ReadDescriptor(layout.StoredFieldOf(layout.EntityType.GetField("Text")));
        Assert.Equal(DescriptorKindEnum.Text, descriptor.Kind);
        var entry = ValueBlocks.AddressOf(descriptor);
        return new TextEntry(arena, entry, ValueBlocks.TextSlotOf(arena, entry), descriptor);
    }

    private static List<ArenaAddress> TextBlocks(TextEntry entry)
    {
        var addresses = new List<ArenaAddress> { entry.Address };
        if (ValueBlocks.LiesInChunks(entry.Descriptor))
        {
            var directory = ValueBlocks.Directory(entry.Arena, entry.Descriptor);
            for (var offset = 0; offset < directory.Length; offset += ValueBlocks.DescriptorSize)
            {
                addresses.Add(ValueBlocks.AddressOf(ResidentDescriptor.Read(directory[offset..])));
            }
        }

        return addresses;
    }

    private static int SlotBytes(Arena arena, ArenaAddress address) => Arena.BlockSizeFor((int)arena.Header(address).PayloadLength);

    private static long CountedSlots(EngineDatabase database)
    {
        var tally = database.Counters.Arena;
        return tally.LiveBytes + tally.ClassSlackBytes + tally.PreparedBytes + tally.RetiredBytes;
    }

    private sealed record TextEntry(Arena Arena, ArenaAddress Address, uint Id, ValueDescriptor Descriptor);

    private sealed record Target(string Text, TextEntry Entry, int Version, int Buckets);
}
