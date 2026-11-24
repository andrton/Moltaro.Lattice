using Moltaro.Lattice.TestSupport.PartialUpdates;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.PartialUpdates;

public sealed class UpdateSubmissionTests
{
    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_command_shape_does_not_change_the_builder(bool byClass)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        var records = database.Set("Item");
        await items.InsertAsync(UpdateSchema.Item("ada"));

        var update = byClass ? items.CreateUpdate("ada") : records.CreateUpdate("ada");
        var city = update.Assign("Home.City", "Paris");
        Assert.Throws<ArgumentException>(() => update.Assign("Home.City", "Rome"));
        Assert.Throws<ArgumentException>(() => update.Assign("Id", "grace"));
        Assert.Throws<ArgumentException>(() => update.Assign("Unknown", 42));
        Assert.Throws<ArgumentException>(() => update.Assign("Home..City", "Rome"));
        Assert.Throws<ArgumentException>(() => update.Assign("Options.City", "Rome"));
        Assert.Throws<ArgumentException>(() => update.Assign("Login.Missing", "Rome"));
        Assert.Throws<ArgumentException>(() => update.Assign("Home", new UpdateHome()));
        Assert.Throws<ArgumentException>(() => update.ListAppend("Tags", "other"));
        Assert.Throws<ArgumentException>(() => update.SetRemove("Scores", 10));
        Assert.Throws<ArgumentException>(() => update.Assign<string?>("Login", null));
        Assert.Throws<ArgumentException>(() => update.Assign<string?>("Scores", null));
        Assert.Throws<ArgumentException>(() => update.ListAppend<int?>("Scores", null));
        Assert.Throws<ArgumentException>(() => update.Assign("Scores", new int?[] { null }));
        Assert.Throws<ArgumentException>(() => update.Assign("Scores", new[] { (short)1 }));
        Assert.Throws<ArgumentException>(() => update.MapRemove<string?>("Options", null));
        Assert.Throws<ArgumentException>(() => update.SetAdd<string?>("Tags", null));
        Assert.Equal(1, update.Count);
        Assert.True((await update.CommitAsync()).Changed);
        Assert.True(city.Changed);
        Assert.Equal("Paris", items.Get("ada")!.Home!.City);
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Adding_commands_copies_binary_whole_collections_and_owned_operands(bool byClass)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var typed = database.Set<UpdateItem>();
        var records = database.Set("Item");
        await typed.InsertAsync(UpdateSchema.Item("ada"));
        Assert.True(database.Schema.TryGetOwnedType("Home", out var homeType));

        var update = byClass ? typed.CreateUpdate("ada") : records.CreateUpdate("ada");
        byte[] data = [1, 2];
        update.Assign("Data", data);
        data[0] = 8;

        var scores = new List<int> { 10 };
        update.Assign("Scores", scores);
        scores[0] = 99;
        scores.Add(100);

        var tags = new[] { "Member", "MEMBER" };
        update.Assign("Tags", tags);
        tags[0] = "Other";

        byte[] ownedBytes = [3, 4];
        if (byClass)
        {
            var home = new UpdateHome { City = "Oslo", Data = ownedBytes };
            var whole = new Dictionary<string, UpdateHome?> { ["First"] = home };
            update.Assign("Options", whole);
            whole.Clear();
            home.City = "Berlin";
            update.MapSet("Options", "Second", home);
            home.City = "Rome";
        }
        else
        {
            var home = new LatticeOwnedValue(homeType) { ["City"] = "Oslo", ["Data"] = ownedBytes };
            var whole = new Dictionary<string, LatticeOwnedValue?> { ["First"] = home };
            update.Assign("Options", whole);
            whole.Clear();
            home["City"] = "Berlin";
            update.MapSet("Options", "Second", home);
            home["City"] = "Rome";
        }

        ownedBytes[0] = 9;
        await update.CommitAsync();

        var stored = typed.Get("ada")!;
        Assert.Equal(new byte[] { 1, 2 }, stored.Data);
        Assert.Equal(10, Assert.Single(stored.Scores!));
        Assert.Equal("Member", Assert.Single(stored.Tags!));
        Assert.Equal(new[] { "First", "Second" }, stored.Options!.Keys);
        Assert.Equal("Oslo", stored.Options["First"]!.City);
        Assert.Equal("Berlin", stored.Options["Second"]!.City);
        Assert.All(stored.Options.Values, value => Assert.Equal(new byte[] { 3, 4 }, value!.Data));
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unset_required_fields_in_owned_operands_fail_at_submission(bool byClass)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var typed = database.Set<UpdateItem>();
        var records = database.Set("Item");
        await typed.InsertAsync(UpdateSchema.Item("ada"));
        Assert.True(database.Schema.TryGetOwnedType("Home", out var homeType));

        var update = byClass ? typed.CreateUpdate("ada") : records.CreateUpdate("ada");
        var command = byClass
            ? update.MapSet("Options", "new", new UpdateHome { City = null! })
            : update.MapSet("Options", "new", new LatticeOwnedValue(homeType) { ["Data"] = Array.Empty<byte>() });

        var before = database.Position;
        var commit = update.CommitAsync();
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => commit);
        Assert.Throws<InvalidOperationException>(() => command.Changed);
        Assert.Equal(before, database.Position);
        Assert.Equal("Theme", Assert.Single(typed.Get("ada")!.Options!).Key);
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData("duplicate Map key")]
    [InlineData("invalid text in replaced Set")]
    [InlineData("invalid text in a removal")]
    public async Task Every_copied_operand_is_validated_before_conditions_cancellation_and_close(string input)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var records = database.Set("Item");
        var update = records.CreateUpdate("absent");
        var batch = database.CreateWriteBatch();
        LatticeCommandResult invalid;
        if (input == "duplicate Map key")
        {
            var map = new Dictionary<string, LatticeOwnedValue?>(StringComparer.Ordinal) { ["Theme"] = null, ["THEME"] = null };
            invalid = update.Assign("Options", map);
            map.Clear();
            update.Assign("Options", map);
        }
        else if (input == "invalid text in replaced Set")
        {
            var tags = new[] { "\uD800" };
            invalid = update.Assign("Tags", tags);
            tags[0] = "valid";
            update.Assign("Tags", tags);
        }
        else
        {
            invalid = update.SetRemove("Tags", "\uD800");
        }

        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => batch.Update(update));
        Assert.Equal(0, batch.Count);
        Assert.Throws<InvalidOperationException>(() => invalid.Changed);
        await database.CloseAsync();

        var commit = update.CommitAsync(default(LatticeRevision), new CancellationToken(canceled: true));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => commit);
        Assert.Throws<InvalidOperationException>(() => invalid.Changed);
        Assert.Throws<InvalidOperationException>(() => { _ = update.CommitAsync(); });
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task A_rejected_batch_add_does_not_consume_the_update_but_a_successful_add_does()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        await using var other = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var typed = database.Set<UpdateItem>();
        await typed.InsertAsync(UpdateSchema.Item("ada"));
        Assert.True(typed.TryGet("ada", out _, out var revision));

        var update = typed.CreateUpdate("ADA");
        Assert.Throws<InvalidOperationException>(() => { _ = update.CommitAsync(); });
        Assert.Throws<InvalidOperationException>(() => database.CreateWriteBatch().Update(update));
        Assert.Throws<ArgumentException>(() => other.CreateWriteBatch().Update(update));
        var command = update.SetAdd("Tags", "member");
        Assert.Throws<ArgumentNullException>(() => { _ = update.CommitAsync(null!); });

        var duplicate = database.CreateWriteBatch();
        duplicate.Delete("Item", "ada");
        Assert.Throws<ArgumentException>(() => duplicate.Update(update));
        Assert.Equal(1, duplicate.Count);
        Assert.Equal(1, update.Count);

        var batch = database.CreateWriteBatch();
        batch.Update(update, revision);
        Assert.Equal(1, batch.Count);
        Assert.Throws<InvalidOperationException>(() => { _ = update.CommitAsync(); });
        Assert.Throws<InvalidOperationException>(() => update.Assign("Label", "later"));
        Assert.Throws<InvalidOperationException>(() => other.CreateWriteBatch().Update(update));
        Assert.Throws<InvalidOperationException>(() => command.Changed);
        Assert.False((await batch.CommitAsync()).Changed);
        Assert.False(command.Changed);
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task Batch_add_checks_core_content_then_batch_state_then_key_before_the_closed_engine()
    {
        await using var database = await TestData.OpenMasterAsync(schema: UpdateSchema.Schema);
        var records = database.Set("Item");
        var submitted = database.CreateWriteBatch();
        submitted.Delete("Item", "ada");
        await submitted.CommitAsync();
        var duplicate = database.CreateWriteBatch();
        duplicate.Delete("Item", "ada");
        var empty = database.CreateWriteBatch();

        var invalid = records.CreateUpdate("ada");
        invalid.Assign("Login", "\uD800");
        var valid = records.CreateUpdate("ADA");
        valid.Assign("Login", "new");
        await database.CloseAsync();

        Assert.Throws<ObjectDisposedException>(() => records.CreateUpdate("ada"));
        valid.Assign("Label", "builder is still local");
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => submitted.Update(invalid));
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => duplicate.Update(invalid));
        Assert.Throws<InvalidOperationException>(() => submitted.Update(valid));
        Assert.Throws<ArgumentException>(() => duplicate.Update(valid));
        Assert.Throws<ObjectDisposedException>(() => empty.Update(valid));
        Assert.Equal(1, submitted.Count);
        Assert.Equal(1, duplicate.Count);
        Assert.Equal(0, empty.Count);
        Assert.Equal(2, valid.Count);
    }
}
