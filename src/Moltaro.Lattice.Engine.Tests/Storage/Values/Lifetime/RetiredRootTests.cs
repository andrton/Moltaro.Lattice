using Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Lifetime;

// A published root that a write replaced or deleted is retired as a whole by RootStorage.Retire: the
// root block and every block it owns, inline values aside, leave the live counters and count with
// their whole slots in RetiredBytes, and the root reads as it did, so that a read that reached it
// before still copies one whole state (arena protocol, "Deferred reclamation, 16 November 2026").
// RootStorage.Free frees a retired root as it frees a live one, and nothing of it is left.
public sealed class RetiredRootTests
{
    private const string Fields = """
        { 'Id': 2, 'Name': 'Login', 'Type': 'String' },
        { 'Id': 3, 'Name': 'Note', 'Type': 'String', 'Nullable': true },
        { 'Id': 4, 'Name': 'Photo', 'Type': 'Binary' }
        """;

    // A value inline, one in a block of its own and one in two chunks with their directory.
    private static readonly string Login = Payloads.Ascii(20);

    private static readonly string Note = Payloads.Ascii(1_000);

    private static readonly byte[] Photo = Payloads.Bytes(70_000);

    [Fact]
    public void Retired_root_counts_its_slots_as_retired_reads_as_before_and_is_freed_whole()
    {
        using var item = RootArena.Of(Fields);
        var writer = item.Writer();

        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Login"), Login);
        writer.SetString(item.Field("Note"), Note);
        writer.SetBinary(item.Field("Photo"), Photo);

        var root = item.Publish(writer.Complete());
        var held = StoredBytes.In(item.Arena);

        RootStorage.Retire(item.Arena, item.Layout, root);

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
        Assert.Equal(held.LiveBytes + held.ClassSlackBytes, item.Arena.Counters.RetiredBytes);

        var reader = item.Reader(root);

        Assert.Equal(Login, reader.GetString(item.Field("Login")));
        Assert.Equal(Note, reader.GetString(item.Field("Note")));
        Assert.Equal(Photo, reader.GetBinary(item.Field("Photo")));

        RootStorage.Free(item.Arena, item.Layout, root);

        Assert.Equal(0, item.Arena.Counters.RetiredBytes);
        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
    }
}
