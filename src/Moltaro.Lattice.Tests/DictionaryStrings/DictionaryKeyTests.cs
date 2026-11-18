using static Moltaro.Lattice.Tests.DictionaryStrings.DictionarySchema;

namespace Moltaro.Lattice.Tests.DictionaryStrings;

// A String primary key may declare Dictionary storage, as any String field may (the schema allows it), and
// then every operation works on it as on a Plain key (brief of Epic 1, sprint S13, Acceptance; brief of
// S07 for the keys): an insert, a read by key and an enumeration, a replace, an upsert and a delete,
// through the class and by name. A key compared without case is found under every spelling, an insert
// under another spelling is DuplicateKey, and a replace and an upsert under another spelling keep the
// spelling the entity was created with, since the primary key is immutable; a delete under another
// spelling deletes. Compared with Ordinal, two spellings are two entities. The text of the key may be
// the text of another Dictionary field of the same or another entity, which it then shares.
public sealed class DictionaryKeyTests
{
    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum, LatticeStringComparisonEnum>> Databases => EveryDatabase;

    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum>> Encodings => EveryEncoding;

    [Theory]
    [MemberData(nameof(Databases))]
    public async Task Entity_with_a_Dictionary_key_is_inserted_read_replaced_upserted_and_deleted(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison)
    {
        await using var database = await OpenAsync(encoding, comparison);
        var codes = database.Set<Code>();
        var records = database.Set("Code");

        // The body of one is the key of the other, which they then share.
        await codes.InsertAsync(new Code { Name = "alpha", Body = "beta" });
        await records.InsertAsync(CodeRecord(records, "beta", "alpha"));
        await codes.InsertAsync(new Code { Name = "Жук", Body = "a😀b" });

        Assert.Equal("beta", codes.Get("alpha")?.Body);
        Assert.Equal("alpha", records.Get("beta")?["Body"]);
        Assert.Equal("a😀b", codes.Get("Жук")?.Body);
        Assert.Equal(["alpha", "beta", "Жук"], codes.Enumerate().Select(code => code.Name).Order(StringComparer.Ordinal));
        Assert.Null(codes.Get("gamma"));

        await codes.ReplaceAsync(new Code { Name = "alpha", Body = "replaced" });
        await records.UpsertAsync(CodeRecord(records, "beta", "upserted"));
        await codes.UpsertAsync(new Code { Name = "gamma", Body = "alpha" });

        Assert.Equal("replaced", codes.Get("alpha")?.Body);
        Assert.Equal("upserted", codes.Get("beta")?.Body);
        Assert.Equal("alpha", records.Get("gamma")?["Body"]);

        Assert.True((await codes.DeleteAsync("alpha")).Changed);
        Assert.True((await records.DeleteAsync("Жук")).Changed);
        Assert.False((await codes.DeleteAsync("alpha")).Changed);

        Assert.Null(codes.Get("alpha"));
        Assert.Null(records.Get("Жук"));
        Assert.Equal(["beta", "gamma"], records.Enumerate().Select(record => (string)record["Name"]!).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Dictionary_key_compared_without_case_is_found_under_every_spelling_and_keeps_the_spelling_it_was_created_with(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding, LatticeStringComparisonEnum.OrdinalIgnoreCase);
        var codes = database.Set<Code>();
        var records = database.Set("Code");

        await codes.InsertAsync(new Code { Name = "Main", Body = "created" });
        await codes.InsertAsync(new Code { Name = "Été", Body = "created" });

        Assert.Equal("Main", codes.Get("MAIN")?.Name);
        Assert.Equal("Été", records.Get("éTÉ")?["Name"]);
        await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => codes.InsertAsync(new Code { Name = "main", Body = "second" }));

        await codes.ReplaceAsync(new Code { Name = "mAIN", Body = "replaced" });
        await records.UpsertAsync(CodeRecord(records, "ÉTÉ", "upserted"));

        Assert.Equal(("Main", "replaced"), (codes.Get("main")!.Name, codes.Get("main")!.Body));
        Assert.Equal(("Été", "upserted"), (codes.Get("été")!.Name, codes.Get("été")!.Body));
        Assert.Equal(["Main", "Été"], codes.Enumerate().Select(code => code.Name).Order(StringComparer.Ordinal));

        // A delete under another spelling deletes, and an insert afterwards stores its own spelling.
        Assert.True((await codes.DeleteAsync("MAIN")).Changed);
        await codes.InsertAsync(new Code { Name = "MAIN", Body = "again" });

        Assert.Equal("MAIN", codes.Get("main")?.Name);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Dictionary_keys_that_differ_only_in_case_are_two_entities_when_compared_with_Ordinal(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding, LatticeStringComparisonEnum.Ordinal);
        var codes = database.Set<Code>();

        await codes.InsertAsync(new Code { Name = "Main", Body = "upper" });
        await codes.InsertAsync(new Code { Name = "main", Body = "lower" });

        Assert.Equal("upper", codes.Get("Main")?.Body);
        Assert.Equal("lower", codes.Get("main")?.Body);
        Assert.Null(codes.Get("MAIN"));

        Assert.True((await codes.DeleteAsync("main")).Changed);

        Assert.Equal("upper", codes.Get("Main")?.Body);
        Assert.Null(codes.Get("main"));
    }
}
