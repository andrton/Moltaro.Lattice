using Moltaro.Lattice.Tests.NestedValues.References;

namespace Moltaro.Lattice.Tests.Views;

// Compile-only test fixture until the analyzer supplies this partial declaration.
internal readonly ref partial struct LinkView : ILatticeEntityView<LinkView, Link>
{
    public long Required => throw new NotSupportedException();

    public sbyte? ToInt8 => throw new NotSupportedException();

    public byte? ToUInt8 => throw new NotSupportedException();

    public short? ToInt16 => throw new NotSupportedException();

    public ushort? ToUInt16 => throw new NotSupportedException();

    public int? ToInt32 => throw new NotSupportedException();

    public uint? ToUInt32 => throw new NotSupportedException();

    public long? ToInt64 => throw new NotSupportedException();

    public ulong? ToUInt64 => throw new NotSupportedException();

    public Guid? ToGuid => throw new NotSupportedException();

    public LatticeValueView ToText => throw new NotSupportedException();

    public LatticeValueView ToName => throw new NotSupportedException();

    public LatticeListView Many => throw new NotSupportedException();

    public LatticeListView Names => throw new NotSupportedException();

    public LatticeListView Guids => throw new NotSupportedException();

    static ReadOnlySpan<LatticeViewField> ILatticeEntityView<LinkView, Link>.Fields => throw new NotSupportedException();

    static LinkView ILatticeEntityView<LinkView, Link>.Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields) =>
        throw new NotSupportedException();
}
