using static Moltaro.Lattice.Tests.Keys.StringKeySchema;

namespace Moltaro.Lattice.Tests.Keys;

// A write forwarded through a child has no rule of its own for the spelling of a key (settled by the
// planner with the implementation of S12, point 2): a replace and an upsert through a child under another
// spelling of a String key compared without case leave the stored entity with the key spelling the same
// write made on the master directly leaves. The test compares the two paths and does not write the rule
// down: on the master, Ada is replaced as ADA and Cid upserted as CID; through the child, Bob is replaced
// as BOB and Dan upserted as DAN; each forwarded write keeps or takes the spelling as its master's
// counterpart does, read on the master and on the child.
public sealed class KeySpellingThroughAChildTests
{
    [Fact]
    public async Task Replace_and_upsert_through_a_child_under_another_spelling_store_the_key_as_the_same_write_on_the_master_does()
    {
        var schema = StringKeySchema.Of(LatticeStringEncodingEnum.Utf8, LatticeStringComparisonEnum.OrdinalIgnoreCase);
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, schema);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var onTheMaster = master.Set<Note>();
        var throughTheChild = child.Set<Note>();

        foreach (var name in new[] { "Ada", "Bob", "Cid", "Dan" })
        {
            await onTheMaster.InsertAsync(new Note { Name = name, Body = "inserted" });
        }

        await MasterAndChild.EventuallyAsync(() => throughTheChild.Get("Dan") is not null);

        await onTheMaster.ReplaceAsync(new Note { Name = "ADA", Body = "replaced" });
        await onTheMaster.UpsertAsync(new Note { Name = "CID", Body = "upserted" });
        await throughTheChild.ReplaceAsync(new Note { Name = "BOB", Body = "replaced" });
        await throughTheChild.UpsertAsync(new Note { Name = "DAN", Body = "upserted" });
        await MasterAndChild.EventuallyAsync(() => throughTheChild.Get("Cid")?.Body == "upserted");

        foreach (var notes in new[] { onTheMaster, throughTheChild })
        {
            Assert.Equal(SpellingOf(notes.Get("ada"), "Ada", "ADA"), SpellingOf(notes.Get("bob"), "Bob", "BOB"));
            Assert.Equal(SpellingOf(notes.Get("cid"), "Cid", "CID"), SpellingOf(notes.Get("dan"), "Dan", "DAN"));
        }
    }

    // Which spelling the stored entity has: the one it was inserted with, the one the write gave, or
    // another, named as it is.
    private static string SpellingOf(Note? stored, string inserted, string written)
    {
        Assert.NotNull(stored);
        Assert.NotEqual("inserted", stored.Body);

        if (stored.Name == inserted)
        {
            return "the inserted spelling";
        }

        return stored.Name == written ? "the written spelling" : stored.Name;
    }
}
