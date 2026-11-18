using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// The keys a pass remembers beyond one page of places (review round 2 of Epic 1, sprint S08, part 2):
// ReturnedKeys keeps its places in pages of 2^20, and a pass needs a second page only once it has
// remembered more than 786,432 keys, three quarters of one page, which no pass of the other tests does
// (ReturnedKeysCapacityTests follows the counts alone). Here every key remembered is found again, and a
// key never remembered is not found, for a key of one word, an Int64, and of two, a Guid, across the
// doublings that take the places from 16 to 2^21, two pages. 1,100,000 keys are more than one page has
// places, so a page index that put two pages onto one would leave the last keys no empty place.
//
// The test drives ReturnedKeys as a step of a pass does (EntityTable.TryReadNext, EntityPass): for each
// key it asks whether the key is remembered, whether there is room for it, makes the room when there
// is none, stages the key and commits it. It needs no database: an entity table of its own, over an
// arena of its own that it never writes, gives the key type and the hash of the index under a fixed
// seed (EntityTable.IntegerHash, GuidHash), and each key is read from a copy of the fixed part of a
// root into which the test writes it (RootReader with a fixed part), where an Int64 and a Guid key lie.
// A String key takes the same pages, as a place of two words as a Guid does; its text lies outside the
// fixed part, so a String key would need a root in the arena for every key, which makes the test
// thirty times as long, and the Guid stands for it here. The work runs on a thread of its own and the
// test waits for it within a bound, so a fault that would place a key forever fails the test instead
// of hanging it.
public sealed class ReturnedKeysPagesTests
{
    private const ulong Seed = 0x9E3779B97F4A7C15;

    private const int Remembered = 1_100_000;

    private const int NeverRemembered = 100_000;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData("Int64")]
    [InlineData("Guid")]
    public async Task Every_key_remembered_beyond_one_page_is_found_again_and_no_other(string keyType)
    {
        var outcome = await OwnThread.Run(() => RememberAndFind(keyType)).WaitAsync(Bound);

        Assert.Equal((0, 0, 0), outcome);
    }

    // Remembers the keys of the numbers 1 to Remembered and asks for them again, and for as many keys
    // that were never remembered: the keys that were found before they were remembered, the remembered
    // keys not found again, and the others found.
    private static (int FoundBefore, int Lost, int FoundNever) RememberAndFind(string keyType)
    {
        var schema = KeySchema.Of("Utf8", "Ordinal", keyType);

        Assert.True(schema.TryGetEntityType(keyType, out var entityType));

        var owner = new object();
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize });
        var roots = new RootTable();
        var epochs = new ReadEpochs(owner);
        var table = EntityTable.Open(entityType, schema, arena, roots, epochs, new RetiredMemory(epochs, arena, roots), new PositionClock(1, owner), Seed);
        var layout = table.Layout!;
        var fixedPart = new byte[RootReader.FixedPartLength(layout)];
        var offset = layout.StoredFieldOf(entityType.Key).Offset - BlockHeader.Size;
        var keys = new ReturnedKeys(table);

        // Writes the key of the number into the fixed part and returns the hash the index gives it.
        uint Write(long number)
        {
            if (keyType == "Int64")
            {
                var key = unchecked(number * (long)0x9E37_79B9_7F4A_7C15);

                BinaryPrimitives.WriteInt64LittleEndian(fixedPart.AsSpan(offset), key);

                return table.IntegerHash(key);
            }

            // The two halves of the Guid, as the engine holds them, both different for every number.
            var first = unchecked(number * (long)0xD1B5_4A32_D192_ED03);
            var second = unchecked((number * (long)0x8CB9_2BA7_2F3D_8DD7) + 1);

            BinaryPrimitives.WriteInt64LittleEndian(fixedPart.AsSpan(offset), first);
            BinaryPrimitives.WriteInt64LittleEndian(fixedPart.AsSpan(offset + 8), second);

            return table.GuidHash(first, second);
        }

        var foundBefore = 0;

        for (var number = 1L; number <= Remembered; number++)
        {
            var hash = Write(number);
            var stored = new RootReader(layout, fixedPart);

            if (keys.Contains(hash, stored))
            {
                foundBefore++;
            }

            if (!keys.HasRoomFor(stored, out var bound))
            {
                keys.MakeRoom(bound);
                Assert.True(keys.HasRoomFor(stored, out bound));
            }

            keys.Stage(hash, stored, bound);
            keys.Commit();
        }

        var lost = 0;

        for (var number = 1L; number <= Remembered; number++)
        {
            var hash = Write(number);

            if (!keys.Contains(hash, new RootReader(layout, fixedPart)))
            {
                lost++;
            }
        }

        var foundNever = 0;

        for (var number = Remembered + 1L; number <= Remembered + NeverRemembered; number++)
        {
            var hash = Write(number);

            if (keys.Contains(hash, new RootReader(layout, fixedPart)))
            {
                foundNever++;
            }
        }

        // The premise: the places have doubled beyond one page.
        Assert.True(keys.BytesWithRoomFor(0) > ReturnedKeys.PlaceBytes(1L << 20, keyType == "Int64" ? 1 : 2), "The places did not grow beyond one page.");

        return (foundBefore, lost, foundNever);
    }
}
