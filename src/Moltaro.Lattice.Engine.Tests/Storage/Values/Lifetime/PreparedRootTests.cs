using Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Lifetime;

// A writer writes a root as a write of the database does: every block it
// allocates is prepared until RootStorage.Publish publishes the root, and then the arena holds, live,
// what the placement of its values gives (StoredBytes.Of; brief of Epic 1, sprint S06, the planner's
// answer to Q30). A prepared root reads like any root. A prepared writer that is abandoned, or
// whose Complete is refused, leaves nothing prepared.
public sealed class PreparedRootTests
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
    public void Root_of_a_prepared_writer_is_prepared_until_published_and_then_held_as_a_root_of_live_blocks()
    {
        using var item = RootArena.Of(Fields);

        // What the root holds by the placement of its values, from its layout: the root block with the
        // fixed part and the inline Login, a block of its own for the Note, and two chunks and their
        // directory for the Photo, 75,184 bytes of slots.
        var held = StoredBytes.Of(item.Layout.FixedSize, 20, 1_000, 70_000);

        var writer = new RootWriter(item.Arena, item.Layout, RootArena.Handle);
        var root = Write(ref writer, item);

        Assert.Equal(held.LiveBytes + held.ClassSlackBytes, item.Arena.Counters.PreparedBytes);
        Assert.Equal((0L, 0L), (item.Arena.Counters.LiveBytes, item.Arena.Counters.ClassSlackBytes));

        RootStorage.Publish(item.Arena, item.Layout, root);

        Assert.Equal(0, item.Arena.Counters.PreparedBytes);
        Assert.Equal(held, StoredBytes.In(item.Arena));
    }

    [Fact]
    public void Prepared_root_reads_like_any_root()
    {
        using var item = RootArena.Of(Fields);

        var writer = new RootWriter(item.Arena, item.Layout, RootArena.Handle);
        var root = Write(ref writer, item);
        var reader = item.Reader(root);

        Assert.Equal(Login, reader.GetString(item.Field("Login")));
        Assert.Equal(Note, reader.GetString(item.Field("Note")));
        Assert.Equal(Photo, reader.GetBinary(item.Field("Photo")));
    }

    [Fact]
    public void Abandoned_or_refused_prepared_writer_leaves_nothing_prepared()
    {
        using var item = RootArena.Of(Fields);

        var abandoned = new RootWriter(item.Arena, item.Layout, RootArena.Handle);
        abandoned.SetInteger(item.Field("Id"), 1);
        abandoned.SetString(item.Field("Login"), Login);
        abandoned.SetString(item.Field("Note"), Note);
        abandoned.SetBinary(item.Field("Photo"), Photo);
        abandoned.Abandon();

        Assert.Equal(0, item.Arena.Counters.PreparedBytes);

        // An arena of one 4 MiB segment has no room for a photo of 5 MiB: Complete is refused for the
        // memory of the arena and frees what was prepared.
        using var small = new RootArena(ItemSchema.Compile(Fields), memoryLimit: RootArena.SegmentSize);
        var refused = new RootWriter(small.Arena, small.Layout, RootArena.Handle);
        refused.SetInteger(small.Field("Id"), 1);
        refused.SetString(small.Field("Login"), Login);
        refused.SetString(small.Field("Note"), Note);
        refused.SetBinary(small.Field("Photo"), Payloads.Bytes(5 * 1024 * 1024));

        WriterAssert.CompleteRefused(ref refused, LatticeErrorEnum.WriteRejected);
        Assert.Equal(0, small.Arena.Counters.PreparedBytes);
        Assert.Equal(StoredBytes.Zero, StoredBytes.In(small.Arena));
    }

    private static ArenaAddress Write(ref RootWriter writer, RootArena item)
    {
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Login"), Login);
        writer.SetString(item.Field("Note"), Note);
        writer.SetBinary(item.Field("Photo"), Photo);

        return writer.Complete();
    }
}
