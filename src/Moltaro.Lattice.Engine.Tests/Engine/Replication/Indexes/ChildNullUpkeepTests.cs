using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Indexes;

// In an entry the null bit decides and a null part is zero, whatever its root holds under the null
// (Storage.Ordered.IndexLayout; docs/design/ordered-index.md, "The entries of one write"), so a replace from
// a null to a null leaves the entry as it was. A root written through the public API holds zeros under a
// null field; a root received from another node holds what that node sent, since a child checks the
// descriptor of a null String or Binary field and not the bytes under a null field of a fixed width
// (Replication.Load.BlockImport). A relay (FrameRelay) writes other bytes than zeros where Value, a nullable
// Int64 of Optional, stands in the root of a replace from null to null that changes only Label, a field in
// no index. The child applies it: no node of ByValue is retired on the child (RetiredNodes), its walk
// (EngineDatabase.IndexNodes) is as before, and a search by null finds the entity.
public sealed class ChildNullUpkeepTests
{
    [Fact]
    public async Task Child_that_receives_other_bytes_under_a_null_key_keeps_the_entry_and_copies_no_node()
    {
        // Set to damage the next transaction the master sends; how many it damaged.
        var damaging = 0;
        var damaged = 0;

        await using var master = await ReplicationNodes.OpenMasterAsync(UpkeepSchema.Schema);
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || Interlocked.Exchange(ref damaging, 0) == 0)
            {
                return [frame];
            }

            Interlocked.Increment(ref damaged);

            return [frame.WithPayload(TransactionPayload.Make(TransactionPayload.SequenceOf(frame), WithBytesUnderTheNull(TransactionPayload.EffectsOf(frame))))];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(relay.Address), new EngineTestInputs());
        var optional = master.Set("Optional");
        var onChild = child.Set("Optional");

        await optional.InsertAsync(OptionalEntities.Of(optional, null, "a"));
        await child.WaitForPositionAsync(master.Position, ReplicationNodes.Bound);
        var walk = IndexContents.Of(child, "Optional", "ByValue").Walk;

        Volatile.Write(ref damaging, 1);

        var nodes = await RetiredNodes.OfAsync(
            child,
            () => onChild.Get(1L) is null ? null : 1L,
            async () =>
            {
                await optional.ReplaceAsync(OptionalEntities.Of(optional, null, "b"));
                await child.WaitForPositionAsync(master.Position, ReplicationNodes.Bound);
            },
            "Optional",
            1);

        Assert.Equal(1, Volatile.Read(ref damaged));
        Assert.Equal(0, nodes);
        Assert.Equal(walk, IndexContents.Of(child, "Optional", "ByValue").Walk);
        Assert.Equal([1L], OptionalEntities.FoundBy(onChild, null));
        Assert.Equal("b", onChild.Get(1L)?["Label"]);
    }

    // The effects of a transaction whose Write carries a root of Optional, with bytes other than zeros where
    // Value stands in the root: as far from the marker, which the root holds once, as the layout places
    // Value from Marker.
    private static byte[] WithBytesUnderTheNull(byte[] effects)
    {
        Assert.True(UpkeepSchema.Schema.TryGetEntityType("Optional", out var type));
        Assert.True(type.TryGetField("Marker", out var marker));
        Assert.True(type.TryGetField("Value", out var value));

        var layout = RootLayout.Compile(type, UpkeepSchema.Schema);
        var at = MarkedRoot.OnlyPlaceOf(effects, BitConverter.GetBytes(UpkeepSchema.Marker), "the marker") + (layout.StoredFieldOf(value).Offset - layout.StoredFieldOf(marker).Offset);

        effects.AsSpan(at, sizeof(long)).Fill(0x5A);

        return effects;
    }
}
