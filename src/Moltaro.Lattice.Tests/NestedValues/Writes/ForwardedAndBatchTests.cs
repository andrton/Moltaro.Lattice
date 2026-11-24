namespace Moltaro.Lattice.Tests.NestedValues.Writes;

// An entity with owned values, Lists and references is written whole by every way a write reaches the
// master: as a member of a write batch, which takes its values when it is added and writes them at its
// commit; as a write a child forwards to its master; and as a member of a batch a child forwards (the
// remarks of ILatticeWriteBatch; docs/decisions/document-model.md, "Atomic WriteBatch within one
// database"). The master and the child then hold the same trees, compared whole by name and through the
// class (NestedSchema).
public sealed class ForwardedAndBatchTests
{
    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Members_of_a_write_batch_with_such_entities_are_written_whole_at_its_commit()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var byClass = database.Set<Person>();
        await byClass.InsertAsync(NestedSchema.Full("replaced"));
        await byClass.InsertAsync(NestedSchema.Full("deleted"));

        var inserted = NestedSchema.Full("inserted");
        var upserted = NestedSchema.Full("upserted");
        upserted.Home = null;
        var replacement = new Person { Code = "replaced", Name = "bare", Tags = ["only"] };

        var batch = database.CreateWriteBatch();
        batch.Insert(inserted);
        batch.Upsert(NestedSchema.RecordOf(database.Schema, upserted));
        batch.Replace(replacement);
        batch.Delete<Person>("deleted");

        // The batch took the values when they were added: a change made afterwards is not written.
        var expectedInserted = NestedSchema.Describe(database.Schema, inserted);
        inserted.Places.Clear();
        inserted.Home!.Lines.Add("after the add");

        await batch.CommitAsync();

        Assert.Equal(expectedInserted, NestedSchema.Describe(byName.Get("inserted")));
        Assert.Equal(NestedSchema.Describe(database.Schema, upserted), NestedSchema.Describe(database.Schema, byClass.Get("upserted")));
        Assert.Equal(NestedSchema.Describe(database.Schema, replacement), NestedSchema.Describe(byName.Get("replaced")));
        Assert.Null(byName.Get("deleted"));
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Write_a_child_forwards_with_such_an_entity_is_written_whole_on_its_master_and_read_back_on_the_child()
    {
        await using var master = await NestedSchema.OpenListeningMasterAsync();
        await using var child = await TestDatabases.OpenChildAsync(master);
        var person = NestedSchema.Full("forwarded");
        var expected = NestedSchema.Describe(child.Schema, person);

        await child.Set<Person>().InsertAsync(person);

        var replacement = NestedSchema.Full("forwarded");
        replacement.Home = null;
        replacement.Tags = [.. Enumerable.Range(0, 1_000).Select(value => $"tag {value}")];
        replacement.Scores = [.. Enumerable.Range(0, 10_000).Select(value => (long)-value)];

        Assert.Equal(expected, NestedSchema.Describe(master.Set("Person").Get("forwarded")));

        var written = await child.Set("Person").ReplaceAsync(NestedSchema.RecordOf(child.Schema, replacement));
        var session = new LatticeSession(written.Position);

        Assert.Equal(NestedSchema.Describe(master.Schema, replacement), NestedSchema.Describe(master.Set("Person").Get("forwarded")));
        Assert.Equal(NestedSchema.Describe(child.Schema, replacement), NestedSchema.Describe((await child.Set("Person").GetAsync("forwarded", session)).Entity));
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Write_batch_a_child_forwards_with_such_entities_is_written_whole_on_its_master_and_its_child()
    {
        await using var master = await NestedSchema.OpenListeningMasterAsync();
        await using var child = await TestDatabases.OpenChildAsync(master);
        var first = NestedSchema.Full("first");
        var second = NestedSchema.Full("second");
        second.Places = [.. Enumerable.Range(0, 500).Select(value => new Address { City = $"city {value}", Lines = ["line"] })];

        var batch = child.CreateWriteBatch();
        batch.Insert(first);
        batch.Insert(NestedSchema.RecordOf(child.Schema, second));
        var committed = await batch.CommitAsync();

        await child.WaitForPositionAsync(committed.Position, TimeSpan.FromMinutes(1));

        foreach (var person in new[] { first, second })
        {
            var expected = NestedSchema.Describe(master.Schema, person);

            Assert.Equal(expected, NestedSchema.Describe(master.Set("Person").Get(person.Code)));
            Assert.Equal(expected, NestedSchema.Describe(child.Set("Person").Get(person.Code)));
            Assert.Equal(expected, NestedSchema.Describe(child.Schema, child.Set<Person>().Get(person.Code)));
        }
    }
}
