using Moltaro.Lattice.Tests.Indexes.ValueBinding;

namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct ScalarItemView : ILatticeEntityView<ScalarItemView, Item>
{
    public bool? Boolean => throw new NotSupportedException();

    public sbyte? Int8 => throw new NotSupportedException();

    public byte? UInt8 => throw new NotSupportedException();

    public short? Int16 => throw new NotSupportedException();

    public ushort? UInt16 => throw new NotSupportedException();

    public int? Int32 => throw new NotSupportedException();

    public uint? UInt32 => throw new NotSupportedException();

    public long? Int64 => throw new NotSupportedException();

    public ulong? UInt64 => throw new NotSupportedException();

    public float? Float32 => throw new NotSupportedException();

    public double? Float64 => throw new NotSupportedException();

    public decimal? Decimal => throw new NotSupportedException();

    public Guid? Guid => throw new NotSupportedException();

    public DateTimeOffset? DateTimeOffset => throw new NotSupportedException();

    public DateOnly? DateOnly => throw new NotSupportedException();

    public TimeOnly? TimeOnly => throw new NotSupportedException();

    public TimeSpan? TimeSpan => throw new NotSupportedException();

    public short? Level => throw new NotSupportedException();

    public ulong? Wide => throw new NotSupportedException();

    public int? Rights => throw new NotSupportedException();

    public LatticeValueView Exact => throw new NotSupportedException();

    public LatticeValueView Folded => throw new NotSupportedException();

    public int Count => throw new NotSupportedException();

    public long Id => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<ScalarItemView, Item>.Fields => throw new NotSupportedException();

    static ScalarItemView ILatticeEntityView<ScalarItemView, Item>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
