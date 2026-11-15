using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage;

// The layout of the structs the engine holds in bulk, measured on the running runtime and
// compared with the milestone contract ("Native layout and allocation"), which asks that the
// actual layout be asserted and not assumed: the 32-byte header at the start of every block in
// native memory, the 48-byte root entry in the managed pages of the root table, and the 16-byte
// descriptor of a String or Binary value in the fixed part of a root. The sizes are those the
// runtime uses for a value of the type, and each offset is measured between a reference to a
// value and a reference to its field, so nothing is taken from the declarations.
public sealed class LayoutTests
{
    [Fact]
    public void The_types_of_the_fields_have_the_widths_the_specification_gives()
    {
        // A relative address and a root handle are each one UInt64; the kind of a block takes two
        // bytes and the state of a root entry four.
        Assert.Equal(8, Unsafe.SizeOf<ArenaAddress>());
        Assert.Equal(8, Unsafe.SizeOf<RootHandle>());
        Assert.Equal(2, Unsafe.SizeOf<BlockKindEnum>());
        Assert.Equal(4, Unsafe.SizeOf<RootStateEnum>());
    }

    [Fact]
    public void A_block_header_takes_32_bytes()
    {
        Assert.Equal(32, Unsafe.SizeOf<BlockHeader>());
    }

    [Fact]
    public void The_fields_of_a_block_header_lie_where_the_specification_puts_them()
    {
        var header = new BlockHeader();

        // Root handle (8), parent address (8), descriptor offset (4), payload length (4), kind (2),
        // flags (2), layout version (4).
        Assert.Equal(0, OffsetOf(ref header, ref header.Root));
        Assert.Equal(8, OffsetOf(ref header, ref header.Parent));
        Assert.Equal(16, OffsetOf(ref header, ref header.DescriptorOffset));
        Assert.Equal(20, OffsetOf(ref header, ref header.PayloadLength));
        Assert.Equal(24, OffsetOf(ref header, ref header.Kind));
        Assert.Equal(26, OffsetOf(ref header, ref header.Flags));
        Assert.Equal(28, OffsetOf(ref header, ref header.LayoutVersion));
    }

    [Fact]
    public void A_root_entry_takes_48_bytes()
    {
        Assert.Equal(48, Unsafe.SizeOf<RootEntry>());
    }

    [Fact]
    public void The_fields_of_a_root_entry_lie_where_the_specification_puts_them()
    {
        var entry = new RootEntry();

        // Root address (8), last mutation position (8), key-descriptor address (8), stored-root id
        // (8), generation (4), type id (4), state (4), claim count (4).
        Assert.Equal(0, OffsetOf(ref entry, ref entry.Root));
        Assert.Equal(8, OffsetOf(ref entry, ref entry.LastMutationSequence));
        Assert.Equal(16, OffsetOf(ref entry, ref entry.KeyDescriptor));
        Assert.Equal(24, OffsetOf(ref entry, ref entry.StoredRootId));
        Assert.Equal(32, OffsetOf(ref entry, ref entry.Generation));
        Assert.Equal(36, OffsetOf(ref entry, ref entry.TypeId));
        Assert.Equal(40, OffsetOf(ref entry, ref entry.State));
        Assert.Equal(44, OffsetOf(ref entry, ref entry.ClaimCount));
    }

    [Fact]
    public void A_value_descriptor_takes_16_bytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<ValueDescriptor>());
    }

    [Fact]
    public void The_fields_of_a_value_descriptor_lie_where_the_specification_puts_them()
    {
        var descriptor = new ValueDescriptor();

        // Kind and flags (4), length or count (4), relative address or stable text handle (8).
        Assert.Equal(4, Unsafe.SizeOf<DescriptorKindEnum>());
        Assert.Equal(0, OffsetOf(ref descriptor, ref descriptor.Kind));
        Assert.Equal(4, OffsetOf(ref descriptor, ref descriptor.Length));
        Assert.Equal(8, OffsetOf(ref descriptor, ref descriptor.Location));
    }

    [Fact]
    public void A_descriptor_of_all_zero_bytes_is_the_descriptor_of_a_null_value()
    {
        // The descriptor of a null String or Binary is all zero (brief of S05, "Where a value goes").
        Assert.Equal(DescriptorKindEnum.Null, default(ValueDescriptor).Kind);
    }

    // How many bytes after the start of the value its field begins.
    private static int OffsetOf<TValue, TField>(ref TValue value, ref TField field) =>
        (int)Unsafe.ByteOffset(ref Unsafe.As<TValue, byte>(ref value), ref Unsafe.As<TField, byte>(ref field));
}
