using System.Text;
using static Moltaro.Lattice.Tests.DictionaryStrings.DictionarySchema;

namespace Moltaro.Lattice.Tests.DictionaryStrings;

// Text of every length reads back as written from a field with Dictionary storage, as it does from a
// Plain field (brief of Epic 1, sprint S13, Acceptance; the planner's decision after the study, point 2:
// a Dictionary text may be as long as a value may be). Null in a nullable field, the empty text and a
// text of one character; texts around each bound by which the engine places a value (RootLayout of the
// engine): 64 bytes, the most a Plain value lies inline; 4,096 bytes, the most a value lies in one block;
// 65,504 bytes, the payload of one chunk; a text of 1 MiB; and texts whose characters cross the
// boundary of a chunk. The bounds are bytes of the stored encoding, so a UTF-8 database is tested one
// byte below, at and above each, and a UTF-16 database one code unit, two bytes, below, at and above.
public sealed class DictionaryTextLengthTests
{
    private const int InlineBytes = 64;

    private const int SingleBlockBytes = 4 * 1024;

    private const int ChunkBytes = 65_504;

    private const int Mebibyte = 1024 * 1024;

    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum>> Encodings => EveryEncoding;

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Null_the_empty_text_and_a_text_of_one_character_read_back_as_written(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var places = database.Set<Place>();
        var records = database.Set("Place");

        await places.InsertAsync(new Place { Id = 1, Region = "", Zone = null });
        await records.InsertAsync(PlaceRecord(records, 2, "n", zone: ""));
        await places.InsertAsync(new Place { Id = 3, Region = "😀", Zone = "é" });

        ExpectText(places, records, 1, "", null);
        ExpectText(places, records, 2, "n", "");
        ExpectText(places, records, 3, "😀", "é");

        // From each to another and back to null.
        await places.ReplaceAsync(new Place { Id = 1, Region = "n", Zone = "" });
        await records.ReplaceAsync(PlaceRecord(records, 2, "", zone: null));
        await places.UpsertAsync(new Place { Id = 3, Region = "", Zone = null });

        ExpectText(places, records, 1, "n", "");
        ExpectText(places, records, 2, "", null);
        ExpectText(places, records, 3, "", null);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Text_of_every_length_around_the_bounds_of_the_storage_reads_back_as_written(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var places = database.Set<Place>();
        var records = database.Set("Place");
        var unit = encoding == LatticeStringEncodingEnum.Utf8 ? 1 : 2;
        var lengths = new[] { InlineBytes, SingleBlockBytes, ChunkBytes }
            .SelectMany(bound => new[] { bound - unit, bound, bound + unit })
            .Append(Mebibyte)
            .ToArray();
        var texts = lengths.Select((bytes, index) => TextOfBytes(bytes, encoding, index)).ToArray();

        for (var id = 0; id < texts.Length; id++)
        {
            await places.InsertAsync(new Place { Id = id, Region = texts[id], Zone = texts[^(id + 1)] });
        }

        for (var id = 0; id < texts.Length; id++)
        {
            ExpectText(places, records, id, texts[id], texts[^(id + 1)]);
        }

        // Each entity takes the text of the next one, and every text keeps another entity.
        for (var id = 0; id < texts.Length; id++)
        {
            await records.ReplaceAsync(PlaceRecord(records, id, texts[(id + 1) % texts.Length], texts[id]));
        }

        for (var id = 0; id < texts.Length; id++)
        {
            ExpectText(places, records, id, texts[(id + 1) % texts.Length], texts[id]);
        }
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Text_whose_characters_cross_the_boundary_of_a_chunk_reads_back_as_written(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var places = database.Set<Place>();
        var records = database.Set("Place");

        // After the one character a, a character of four bytes in both encodings, or of three in UTF-8,
        // lies across the boundary of 65,504 bytes, and across every later boundary of a chunk too.
        var pairs = "a" + string.Concat(Enumerable.Repeat("😀", 40_000));
        var threes = string.Concat(Enumerable.Repeat("€", 60_000));

        await places.InsertAsync(new Place { Id = 1, Region = pairs, Zone = threes });
        await places.InsertAsync(new Place { Id = 2, Region = threes, Zone = pairs });

        ExpectText(places, records, 1, pairs, threes);
        ExpectText(places, records, 2, threes, pairs);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Large_text_that_two_entities_share_reads_back_as_written_after_one_of_them_is_deleted(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var places = database.Set<Place>();
        var records = database.Set("Place");
        var large = TextOfBytes(Mebibyte, encoding, 7);

        await places.InsertAsync(new Place { Id = 1, Region = large });
        await records.InsertAsync(PlaceRecord(records, 2, large, large));

        ExpectText(places, records, 1, large, null);
        ExpectText(places, records, 2, large, large);

        Assert.True((await places.DeleteAsync(1)).Changed);

        ExpectText(places, records, 2, large, large);
    }

    // A text of ASCII letters that takes this many bytes in the encoding, its letters shifted by the
    // number given, so that two texts of one length differ.
    private static string TextOfBytes(int bytes, LatticeStringEncodingEnum encoding, int shift)
    {
        var characters = encoding == LatticeStringEncodingEnum.Utf8 ? bytes : bytes / 2;
        var text = string.Create(characters, shift, static (span, shift) =>
        {
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = (char)('a' + ((index + shift) % 26));
            }
        });

        Assert.Equal(bytes, encoding == LatticeStringEncodingEnum.Utf8 ? Encoding.UTF8.GetByteCount(text) : Encoding.Unicode.GetByteCount(text));

        return text;
    }

    // The entity of the id has the texts given, through the class and by name.
    private static void ExpectText(ILatticeSet<Place> places, ILatticeSet records, long id, string region, string? zone)
    {
        var place = places.Get(id);
        var record = records.Get(id);

        Assert.NotNull(place);
        Assert.True(region == place.Region, $"Region of {id} read back {place.Region.Length} characters, not the {region.Length} written.");
        Assert.True(zone == place.Zone, $"Zone of {id} did not read back as written.");
        Assert.NotNull(record);
        Assert.True(region == (string?)record["Region"], $"Region of {id} by name did not read back as written.");
        Assert.True(zone == (string?)record["Zone"], $"Zone of {id} by name did not read back as written.");
    }
}
