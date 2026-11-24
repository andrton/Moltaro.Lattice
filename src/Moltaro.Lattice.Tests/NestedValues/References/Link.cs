namespace Moltaro.Lattice.Tests.NestedValues.References;

// The class of the entity type Link of ReferenceSchema: a reference is a property of the key type of its
// target, Nullable of it when the reference is nullable and the key a value type.
internal sealed class Link
{
    public string Code { get; set; } = "";

    public sbyte? ToInt8 { get; set; }

    public byte? ToUInt8 { get; set; }

    public short? ToInt16 { get; set; }

    public ushort? ToUInt16 { get; set; }

    public int? ToInt32 { get; set; }

    public uint? ToUInt32 { get; set; }

    public long? ToInt64 { get; set; }

    public ulong? ToUInt64 { get; set; }

    public Guid? ToGuid { get; set; }

    public string? ToText { get; set; }

    public string? ToName { get; set; }

    public long Required { get; set; }

    public List<long> Many { get; set; } = [];

    public List<string> Names { get; set; } = [];

    public List<Guid?> Guids { get; set; } = [];
}
