using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// A read by key keeps the fields it loads before the secondary indexes of its table (EntityTable, the
// comment at _indexes). The runtime lays out the references of a class in the order of their declaration,
// so the field of the secondary indexes, which a read by key never loads, is declared after the references
// a read by key loads at every call: the arena, the root table, the protection of reads, the clock, the
// primary index and the root layout. State added to the table after the indexes then leaves those
// references at their offsets, and with them the code of the read by key that was measured (EntityTable.
// TryRead, its JIT listing at full optimization); a reference declared before them would move them all.
// The primary keys, held by value, are not among them: the runtime puts a field of a struct after every
// reference wherever it is declared. The test measures where the runtime puts each field in a table it
// opens, as the bytes from a reference to the field to a reference to _indexes, taken through the fields
// themselves (UnsafeAccessor) and not from the declarations: a field the read loads moved after _indexes,
// or _indexes moved before one of them, fails it.
public sealed class TableFieldOrderTests
{
    [Fact]
    public void Every_field_a_read_by_key_loads_lies_before_the_secondary_indexes_of_its_table()
    {
        var schema = KeySchema.Of("Utf8", "Ordinal", "Int64");
        Assert.True(schema.TryGetEntityType("Int64", out var entityType));
        var owner = new object();
        var roots = new RootTable();
        var epochs = new ReadEpochs(owner);
        using var arena = new Arena(new ArenaOptions { MemoryLimit = new ArenaOptions().SegmentSize });
        var table = EntityTable.Open(entityType, schema, arena, roots, epochs, new RetiredMemory(epochs, arena, roots), new PositionClock(1, owner), seed: 0);

        ref var indexes = ref Unsafe.As<SecondaryIndex[], byte>(ref IndexesOf(table));

        (string Name, long BytesBefore)[] loaded =
        [
            ("_arena", BytesBefore(ref ArenaOf(table), ref indexes)),
            ("_roots", BytesBefore(ref RootsOf(table), ref indexes)),
            ("_epochs", BytesBefore(ref EpochsOf(table), ref indexes)),
            ("_clock", BytesBefore(ref ClockOf(table), ref indexes)),
            ("_index", BytesBefore(ref PrimaryIndexOf(table), ref indexes)),
            ("Layout", BytesBefore(ref LayoutOf(table), ref indexes)),
        ];

        Assert.Empty(loaded.Where(field => field.BytesBefore <= 0).Select(field => $"{field.Name}, {-field.BytesBefore} bytes after _indexes"));
    }

    // How many bytes the field begins before the reference given; negative when it begins after it.
    private static long BytesBefore<TField>(ref TField field, ref byte reference) =>
        (long)Unsafe.ByteOffset(ref Unsafe.As<TField, byte>(ref field), ref reference);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_arena")]
    private static extern ref Arena ArenaOf(EntityTable table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_roots")]
    private static extern ref RootTable RootsOf(EntityTable table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_epochs")]
    private static extern ref ReadEpochs EpochsOf(EntityTable table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref PositionClock ClockOf(EntityTable table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_index")]
    private static extern ref PrimaryIndex? PrimaryIndexOf(EntityTable table);

    // The field the compiler makes for the property Layout.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Layout>k__BackingField")]
    private static extern ref RootLayout? LayoutOf(EntityTable table);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_indexes")]
    private static extern ref SecondaryIndex[] IndexesOf(EntityTable table);
}
