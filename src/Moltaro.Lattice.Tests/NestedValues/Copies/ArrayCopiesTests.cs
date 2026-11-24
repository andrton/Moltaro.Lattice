namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// Reads return owned copies, and a write keeps nothing of the caller's, for the arrays of a class as for its
// Lists (AGENTS.md, principle 3; docs/decisions/mutation-commands.md, "Stable write inputs until
// completion"): nothing a caller changes in an object after its write returned changes what was written, and
// two reads through a class share no array, at any depth: an array of Int64 (Numbers), of arrays (Matrix), of
// byte arrays (Blobs) and of owned values (Labels), an array of owned values in a List (LabelRows), and the
// byte arrays of an owned value, its own (Data) and in a List (Files), in a property, in an array and in a
// List (FormSchema). By name, the byte arrays of owned values in Lists are the record's own: two reads share
// none, and a change made through a record after its write returned changes nothing stored.
public sealed class ArrayCopiesTests
{
    [Fact]
    public async Task Nothing_a_caller_changes_in_the_arrays_of_an_object_after_its_write_returned_changes_what_was_written()
    {
        await using var database = await TestDatabases.OpenMasterAsync(FormSchema.Schema);
        var forms = database.Set<Form>();
        var form = Full();
        var written = Describe(form);

        await forms.InsertAsync(form);
        Change(form);

        Assert.Equal(written, Describe(forms.Get(1L)!));
    }

    [Fact]
    public async Task Two_reads_through_a_class_share_no_array_at_any_depth()
    {
        await using var database = await TestDatabases.OpenMasterAsync(FormSchema.Schema);
        var forms = database.Set<Form>();
        await forms.InsertAsync(Full());
        var stored = Describe(forms.Get(1L)!);

        var first = forms.Get(1L)!;
        var second = forms.Get(1L)!;
        Change(first);

        Assert.Equal(stored, Describe(second));
        Assert.Equal(stored, Describe(forms.Get(1L)!));
        Assert.Equal(stored, Describe(forms.Enumerate().Single()));
    }

    [Fact]
    public async Task Byte_arrays_of_owned_values_in_Lists_read_by_name_are_the_records_own()
    {
        await using var database = await TestDatabases.OpenMasterAsync(FormSchema.Schema);
        var forms = database.Set<Form>();
        var byName = database.Set("Form");
        await forms.InsertAsync(Full());
        var stored = Describe(forms.Get(1L)!);

        // A change through one read is seen neither by another nor by the database.
        var first = byName.Get(1L)!;
        var second = byName.Get(1L)!;
        var label = first.Get<List<LatticeOwnedValue>>("Labels")![0];

        Assert.NotSame(label.Get<byte[]>("Data"), second.Get<List<LatticeOwnedValue>>("Labels")![0].Get<byte[]>("Data"));

        label.Get<byte[]>("Data")![0] = 0xEE;
        label.Get<List<byte[]>>("Files")![1][0] = 0xEE;
        first.Get<List<List<LatticeOwnedValue>>>("LabelRows")![1][1].Get<byte[]>("Data")![0] = 0xEE;
        first.Get<LatticeOwnedValue>("Main")!.Get<List<byte[]>>("Files")![0][0] = 0xEE;

        Assert.Equal(stored, Describe(forms.Get(1L)!));

        // Written back by name, and changed through the record after its write returned: nothing stored changes.
        var record = byName.Get(1L)!;
        await byName.ReplaceAsync(record);

        record.Get<List<LatticeOwnedValue>>("Labels")![0].Get<byte[]>("Data")![0] = 0xDD;
        record.Get<List<List<LatticeOwnedValue>>>("LabelRows")![1][0].Get<List<byte[]>>("Files")![1][0] = 0xDD;
        record.Get<List<List<int>>>("Matrix")![1][0] = 55;

        Assert.Equal(stored, Describe(forms.Get(1L)!));
    }

    private static Form Full() => new()
    {
        Id = 1,
        Numbers = [3, -1, 3],
        Matrix = [[], [1, 2], [2, 2]],
        Blobs = [[], [7], [8, 9]],
        Main = LabelOf("main", 50),
        Labels = [LabelOf("a", 10), LabelOf("b", 20)],
        LabelRows = [[], [LabelOf("c", 30), LabelOf("d", 40)]],
    };

    private static Label LabelOf(string text, int seed) => new()
    {
        Text = text,
        Codes = [seed, seed + 1],
        Data = [(byte)seed, (byte)(seed + 1)],
        Files = [[(byte)seed], [(byte)(seed + 2), (byte)(seed + 3)]],
    };

    // A change in every array of the form, at every depth.
    private static void Change(Form form)
    {
        form.Numbers[0] = 99;
        form.Matrix[1][0] = 99;
        form.Blobs[2][0] = 0xFF;
        form.Labels[0].Codes[0] = 99;
        form.Labels[0].Data![0] = 0xFF;
        form.Labels[0].Files[1][0] = 0xFF;
        form.Labels[1].Text = "changed";
        form.LabelRows[1][0].Codes[1] = 99;
        form.LabelRows[1][1].Data![1] = 0xFF;
        form.LabelRows[1][1].Files[0][0] = 0xFF;
        form.Main.Codes[0] = 99;
        form.Main.Data![0] = 0xFF;
        form.Main.Files[1][1] = 0xFF;
    }

    // The arrays of the form, and of its Labels, as one text.
    private static string Describe(Form form) => string.Join(
        "; ",
        string.Join(",", form.Numbers),
        string.Join("/", form.Matrix.Select(row => string.Join(",", row))),
        string.Join("/", form.Blobs.Select(Convert.ToHexString)),
        string.Join("/", form.Labels.Select(Describe)),
        string.Join("//", form.LabelRows.Select(row => string.Join("/", row.Select(Describe)))),
        Describe(form.Main));

    private static string Describe(Label label) =>
        $"{label.Text}|{string.Join(",", label.Codes)}|{(label.Data is null ? "null" : Convert.ToHexString(label.Data))}|{string.Join(",", label.Files.Select(Convert.ToHexString))}";
}
