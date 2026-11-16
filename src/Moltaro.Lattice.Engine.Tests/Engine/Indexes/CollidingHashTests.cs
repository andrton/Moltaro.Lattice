namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Keys of a structure do not cost more of the primary index than other keys (review round 1 of Epic 1,
// sprint S08, part 1: keys (shard << 32) | id of 16 shards took 139 bytes of the index an entry,
// against 22 to 28 for ordinary keys, because Int64.GetHashCode folded them onto few hashes). The
// engine mixes every bit of an Int64 key into its hash, so such keys spread over the index as any
// others do; the bounds hold even where hashes do collide, because the main table grows by the number
// of entries, to twice the buckets once it holds four entries a main bucket, and the entries of one
// hash that do not fit their bucket of one cache line, 64 bytes with seven entries and a link, grow its
// chain of overflow buckets and nothing else (decision on index structures, the addition of 16
// November 2026; chains of one hash given to the index directly: OneHashChainTests):
//
// - Composite keys, 16 shards of 125 ids, 2,000 entities, at most 64 bytes of the index an entity.
//   After a growth the main table holds at least two entries a main bucket: at most 32 bytes an
//   entry. Were the hash shard ^ id, as the hash code is, it would be shared by up to 16 keys, and a
//   chain would need at most two overflow buckets for its 16 entries: at most 8 bytes an entry. The
//   overflow every table keeps, one bucket for every 16 main buckets, adds at most 2. That is at most
//   42 bytes an entry; 64 leaves room for the alignment of the table and for where the count stands
//   between two growths. A table that doubled until its overflow held every chain took about 139.
// - 300 keys whose hash codes are all 0 take at most twice the index that 300 small keys take. The
//   main table is the same for both, since it grows by the count. Were they of one hash, the one chain
//   would need one overflow bucket for every seven entries beyond the first seven, about 9 bytes an
//   entry, while a table of 300 keys of different hashes holds at most four entries a main bucket, at
//   least 16 bytes an entry: at most 1.6 times as much. A table that doubled until its overflow held
//   the chain took about eight times as much.
//
// The bytes are read after a drain with no read in progress, so that no replaced table is counted.
// After the keys whose hash codes are 0 are deleted and a drain, nothing of them is left.
public sealed class CollidingHashTests
{
    private const int Shards = 16;

    private const int IdsPerShard = 125;

    private const int MostBytesPerEntity = 64;

    private const int ZeroHashCodeCount = 300;

    private const int MostTimesTheCount = 2;

    [Fact]
    public async Task Composite_keys_whose_hash_codes_collide_take_at_most_64_bytes_of_the_primary_index_an_entity()
    {
        await using var database = await ClashSchema.OpenAsync();
        var clashes = database.Set<ClashSchema.Clash>();

        for (var shard = 0; shard < Shards; shard++)
        {
            for (var id = 0; id < IdsPerShard; id++)
            {
                await clashes.InsertAsync(ClashSchema.Of(ClashSchema.Composite(shard, id)));
            }
        }

        database.DrainRetired();
        var counters = database.Counters;
        const int Entities = Shards * IdsPerShard;

        Assert.Equal(Entities, counters.RootEntries);
        Assert.True(
            counters.PrimaryIndexBytes <= (long)MostBytesPerEntity * Entities,
            $"The primary index takes {counters.PrimaryIndexBytes / (double)Entities:F1} bytes an entity for {Entities} composite keys, more than {MostBytesPerEntity}.");
    }

    [Fact]
    public async Task Keys_whose_hash_codes_are_all_zero_take_at_most_twice_the_primary_index_their_count_needs_and_leave_nothing_once_deleted()
    {
        var needed = await IndexBytesOfAsync(Enumerable.Range(1, ZeroHashCodeCount).Select(number => (long)number));

        await using var database = await ClashSchema.OpenAsync();
        var clashes = database.Set<ClashSchema.Clash>();
        long[] keys = [.. Enumerable.Range(1, ZeroHashCodeCount).Select(ClashSchema.ZeroHashCode)];

        foreach (var key in keys)
        {
            await clashes.InsertAsync(ClashSchema.Of(key));
        }

        database.DrainRetired();
        var held = database.Counters;

        Assert.Equal(ZeroHashCodeCount, held.RootEntries);
        Assert.True(
            held.PrimaryIndexBytes <= MostTimesTheCount * needed,
            $"The primary index takes {held.PrimaryIndexBytes} bytes for {ZeroHashCodeCount} keys whose hash codes are 0, more than {MostTimesTheCount} times the {needed} that as many small keys take.");

        foreach (var key in keys)
        {
            Assert.True((await clashes.DeleteAsync(key)).Changed);
        }

        database.DrainRetired();
        var after = database.Counters;

        Assert.Equal(0, after.RootEntries);
        Assert.Equal(0, after.Arena.LiveBytes);
        Assert.Equal(0, after.Arena.ClassSlackBytes);
        Assert.Equal(0, after.Arena.RetiredBytes);
        Assert.Equal(0, after.RetiredIndexBytes);
    }

    // The bytes of the primary index of a database of its own that holds these keys, after a drain.
    private static async Task<long> IndexBytesOfAsync(IEnumerable<long> keys)
    {
        await using var database = await ClashSchema.OpenAsync();
        var clashes = database.Set<ClashSchema.Clash>();

        foreach (var key in keys)
        {
            await clashes.InsertAsync(ClashSchema.Of(key));
        }

        database.DrainRetired();

        return database.Counters.PrimaryIndexBytes;
    }
}
