namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// Entities by name, as records: database.Set("Setting"). No .NET class is involved.
public sealed class DynamicSetTests
{
    [Fact]
    public async Task Entity_type_without_a_class_supports_full_crud()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");

        // Create
        await settings.InsertAsync(new LatticeRecord(settings.EntityType)
        {
            ["Name"] = "theme",
            ["Value"] = "dark",
            ["Priority"] = 10,
        });

        // Read
        Assert.True(settings.TryGet("theme", out var record, out var revision));
        Assert.Equal("dark", record["Value"]);
        Assert.Equal(10, record.Get<int>("Priority"));
        Assert.Null(record["Payload"]);

        // Update
        record["Value"] = "light";
        await settings.ReplaceAsync(record, revision);
        Assert.True(settings.TryGet("theme", out var updated));
        Assert.Equal("light", updated["Value"]);

        // Upsert, enumerate
        await settings.UpsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = "language", ["Value"] = "en", ["Priority"] = 1 });
        Assert.Equal(["language", "theme"], settings.Enumerate().Select(item => item.Get<string>("Name")).Order());

        // Delete
        Assert.True((await settings.DeleteAsync("theme")).Changed);
        Assert.False((await settings.DeleteAsync("theme")).Changed);
    }

    [Fact]
    public async Task Get_returns_the_record_or_null()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");
        await settings.InsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 });

        Assert.Equal("dark", settings.Get("theme")?["Value"]);
        Assert.Null(settings.Get("missing"));
    }

    [Fact]
    public async Task Records_follow_the_same_write_rules_as_classes()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");
        LatticeRecord NewSetting(string name) => new(settings.EntityType) { ["Name"] = name, ["Value"] = "v", ["Priority"] = 0 };

        await settings.InsertAsync(NewSetting("a"));
        Assert.True(settings.TryGet("a", out var record, out var staleRevision));
        await settings.ReplaceAsync(record);

        await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => settings.InsertAsync(NewSetting("a")));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => settings.ReplaceAsync(NewSetting("missing")));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => settings.ReplaceAsync(record, staleRevision));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => settings.DeleteAsync("a", staleRevision));
    }

    [Fact]
    public async Task Required_field_without_a_value_is_rejected()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");

        // Priority is required and was not set.
        var incomplete = new LatticeRecord(settings.EntityType) { ["Name"] = "theme", ["Value"] = "dark" };

        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => settings.InsertAsync(incomplete));
        Assert.Empty(settings.Enumerate());
    }

    [Fact]
    public async Task Record_accepts_only_declared_fields_and_matching_values()
    {
        await using var database = await TestData.OpenMasterAsync();
        var record = new LatticeRecord(database.Set("Setting").EntityType);

        Assert.Throws<ArgumentException>(() => record["Unknown"] = 1);
        Assert.Throws<ArgumentException>(() => record["Priority"] = "high");
        Assert.Throws<ArgumentException>(() => record["Priority"] = long.MaxValue);

        // An integer of another width is accepted when it fits the field.
        record["Priority"] = 5L;
        Assert.Equal(5, record["Priority"]);
    }

    [Fact]
    public async Task The_database_keeps_its_own_copy_of_every_record()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");
        var payload = new byte[] { 1, 2, 3 };
        var original = new LatticeRecord(settings.EntityType) { ["Name"] = "blob", ["Value"] = "v", ["Priority"] = 0, ["Payload"] = payload };
        await settings.InsertAsync(original);

        // Changing the submitted bytes, the submitted record or a read result changes nothing stored.
        payload[0] = 99;
        original["Value"] = "changed after insert";
        Assert.True(settings.TryGet("blob", out var firstRead));
        firstRead["Value"] = "changed after read";
        firstRead.Get<byte[]>("Payload")![1] = 99;

        Assert.True(settings.TryGet("blob", out var secondRead));
        Assert.Equal("v", secondRead["Value"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, secondRead.Get<byte[]>("Payload"));
    }

    [Fact]
    public async Task Class_and_name_give_the_same_data()
    {
        await using var database = await TestData.OpenMasterAsync();
        var users = database.Set<User>();
        var userRecords = database.Set("User");

        // Written through the class, read by name.
        await users.InsertAsync(new User { Id = 1, Name = "Ada", Age = 36 });
        Assert.True(userRecords.TryGet(1, out var record, out var revision));
        Assert.Equal("Ada", record["Name"]);
        Assert.Equal(36, record["Age"]);

        // Written by name, read through the class. The revision is the same for both.
        record["Name"] = "Ada Lovelace";
        await userRecords.ReplaceAsync(record, revision);
        Assert.True(users.TryGet(1, out var user));
        Assert.Equal("Ada Lovelace", user.Name);
    }

    [Fact]
    public async Task Unknown_entity_type_is_rejected()
    {
        await using var database = await TestData.OpenMasterAsync();

        TestData.ExpectError(LatticeErrorEnum.BindingFailed, () => database.Set("Invoice"));
    }

    [Fact]
    public async Task Record_of_another_entity_type_is_rejected()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");
        var tag = new LatticeRecord(database.Set("Tag").EntityType) { ["Name"] = "urgent", ["Color"] = "red" };

        await Assert.ThrowsAsync<ArgumentException>(async () => await settings.InsertAsync(tag));
    }

    [Fact]
    public async Task Child_serves_records_by_name_with_the_schema_of_its_master()
    {
        var address = MasterAndChild.NewAddress();
        await using var master = await TestData.OpenMasterAsync(address);
        var masterSettings = master.Set("Setting");
        await masterSettings.InsertAsync(new LatticeRecord(masterSettings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 });

        await using var child = await TestData.OpenChildAsync(address);

        Assert.Equal(["User", "Tag", "Setting"], child.Schema.EntityTypes.Select(entityType => entityType.Name));
        Assert.True(child.Set("Setting").TryGet("theme", out var record));
        Assert.Equal("dark", record["Value"]);
    }
}
