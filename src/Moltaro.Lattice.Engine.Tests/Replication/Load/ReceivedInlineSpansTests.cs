using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// The values inline of a structure a node receives lie one after the other, from the end of the fixed part of
// the root or of the records of a block to the end of the block, each byte named by one value (BlockImport.Check):
// a master writes them so, in the order it places them, which need not be the order of their descriptors. A
// value inline at the location of another, which leaves as many bytes named by nothing, is what a master cannot
// have written: it is refused with ValidationFailed before anything is allocated, in the root payload as in a
// block of records.
//
// Pair: Id, an Int64; B and A, Strings, declared in that order with the identities 3 and 2, so that the
// descriptor of A comes first and the writer places B first. Tags: Id, an Int64; Tags, a List of String. Notes:
// Id, an Int64; Notes, a List of the owned type Note, whose fields B and A are declared as those of Pair.
public sealed class ReceivedInlineSpansTests
{
    private const int Version = 4;

    private static readonly LatticeSchema Pairs = ItemSchema.Compile(
        "{ 'Id': 3, 'Name': 'B', 'Type': 'String' }, { 'Id': 2, 'Name': 'A', 'Type': 'String' }");

    private static readonly LatticeSchema Tags = ItemSchema.Compile("{ 'Id': 2, 'Name': 'Tags', 'Type': 'List', 'Element': { 'Type': 'String' } }");

    private static readonly LatticeSchema Notes = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Notes', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Note' } }",
        ownedTypes: "{ 'Id': 1, 'Name': 'Note', 'Fields': [ { 'Id': 3, 'Name': 'B', 'Type': 'String' }, { 'Id': 2, 'Name': 'A', 'Type': 'String' } ] }");

    public static TheoryData<string> Overlaps =>
    [
        "a value inline in the root payload at the location of another",
        "a value inline in a block of records at the location of another",
    ];

    [Fact]
    public void Values_inline_a_master_wrote_out_of_the_order_of_their_descriptors_are_received_whole()
    {
        foreach (var (schema, record) in new[] { (Pairs, PairOf()), (Notes, NotesOf()) })
        {
            using var item = new RootArena(schema);
            var input = InputOf(item, record(item.EntityType));
            var import = new BlockImport(item.Arena);
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(input));
            var root = import.Place(import.Check(ref reader, item.Layout), RootArena.Handle, out _);

            Assert.False(root.IsNull);
            RootStorage.Free(item.Arena, item.Layout, root);
        }
    }

    [Theory]
    [MemberData(nameof(Overlaps))]
    public void Value_inline_at_the_location_of_another_is_refused_before_anything_is_allocated(string damage)
    {
        var inRoot = damage.Contains("root payload", StringComparison.Ordinal);
        using var item = new RootArena(inRoot ? Pairs : Tags);
        var input = InputOf(item, inRoot ? PairOf()(item.EntityType) : new LatticeRecord(item.EntityType) { ["Id"] = 1L, ["Tags"] = new[] { "ab", "cd" } });
        var (first, second) = inRoot ? InlineOfRoot(item, input) : InlineOfRecords(item);

        // The second value inline given the location of the first: the lengths still add up to the block.
        Assert.Equal(BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(first + 8)) + 2, BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(second + 8)));
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(second + 8), BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(first + 8)));

        var import = new BlockImport(item.Arena);
        var before = item.Arena.Counters;

        var exception = Assert.Throws<LatticeException>(() =>
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(input));

            import.Check(ref reader, item.Layout);
        });

        Assert.Equal(LatticeErrorEnum.ValidationFailed, exception.Error);
        Assert.Equal(before, item.Arena.Counters);
    }

    // Pair with B "cd" and A "ab": the writer places B, the first field, at the end of the fixed part, and A
    // after it.
    private static Func<LatticeEntityType, LatticeRecord> PairOf() => entityType => new LatticeRecord(entityType) { ["Id"] = 1L, ["B"] = "cd", ["A"] = "ab" };

    // Notes with two notes, each B "cd" and A "ab", inline in the one block of the records.
    private static Func<LatticeEntityType, LatticeRecord> NotesOf() => entityType =>
    {
        Assert.True(Notes.TryGetOwnedType("Note", out var note));

        return new LatticeRecord(entityType)
        {
            ["Id"] = 1L,
            ["Notes"] = new[] { new LatticeOwnedValue(note) { ["B"] = "cd", ["A"] = "ab" }, new LatticeOwnedValue(note) { ["B"] = "ef", ["A"] = "gh" } },
        };
    };

    // The root of the record in the load format, after its envelope.
    private static byte[] InputOf(RootArena item, LatticeRecord record) =>
        RootValues.Export(item.Layout, item.EntityType, [.. item.EntityType.Fields.Select(field => record[field.Name])]).ToArray();

    // Where the descriptors of B and of A lie on the wire: B, placed first, lies first, and A after it.
    private static (int First, int Second) InlineOfRoot(RootArena item, byte[] input)
    {
        Assert.True(item.EntityType.TryGetField("B", out var b));
        Assert.True(item.EntityType.TryGetField("A", out var a));

        return (Version + item.Layout.StoredFieldOf([b]).Offset - BlockHeader.Size, Version + item.Layout.StoredFieldOf([a]).Offset - BlockHeader.Size);
    }

    // Where the descriptors of the two records of Tags lie on the wire: after the layout version and the root
    // payload, the Id and the descriptor of Tags, 16 bytes each.
    private static (int First, int Second) InlineOfRecords(RootArena item)
    {
        var records = Version + RootReader.FixedPartLength(item.Layout);

        return (records, records + ValueBlocks.DescriptorSize);
    }
}
