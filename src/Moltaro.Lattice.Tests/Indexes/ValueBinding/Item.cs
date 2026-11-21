namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// Bound to the entity type "Item" of BindingSchema: a property for each field, named as the field. An
// Enum field holds the integer of its enum type. Of and ValueOf reach a property by the name of its field,
// so that a test written for any field sets and reads it through the class as a record does by name.
internal sealed class Item
{
    public long Id { get; set; }

    public bool? Boolean { get; set; }

    public sbyte? Int8 { get; set; }

    public byte? UInt8 { get; set; }

    public short? Int16 { get; set; }

    public ushort? UInt16 { get; set; }

    public int? Int32 { get; set; }

    public uint? UInt32 { get; set; }

    public long? Int64 { get; set; }

    public ulong? UInt64 { get; set; }

    public float? Float32 { get; set; }

    public double? Float64 { get; set; }

    public decimal? Decimal { get; set; }

    public Guid? Guid { get; set; }

    public DateTimeOffset? DateTimeOffset { get; set; }

    public DateOnly? DateOnly { get; set; }

    public TimeOnly? TimeOnly { get; set; }

    public TimeSpan? TimeSpan { get; set; }

    public short? Level { get; set; }

    public ulong? Wide { get; set; }

    public int? Rights { get; set; }

    public string? Exact { get; set; }

    public string? Folded { get; set; }

    public int Count { get; set; }

    // An item with the id and the value of one field, the others without a value.
    public static Item Of(long id, string field, object? value)
    {
        var item = new Item { Id = id };

        switch (field)
        {
            case "Boolean": item.Boolean = (bool?)value; break;
            case "Int8": item.Int8 = (sbyte?)value; break;
            case "UInt8": item.UInt8 = (byte?)value; break;
            case "Int16": item.Int16 = (short?)value; break;
            case "UInt16": item.UInt16 = (ushort?)value; break;
            case "Int32": item.Int32 = (int?)value; break;
            case "UInt32": item.UInt32 = (uint?)value; break;
            case "Int64": item.Int64 = (long?)value; break;
            case "UInt64": item.UInt64 = (ulong?)value; break;
            case "Float32": item.Float32 = (float?)value; break;
            case "Float64": item.Float64 = (double?)value; break;
            case "Decimal": item.Decimal = (decimal?)value; break;
            case "Guid": item.Guid = (Guid?)value; break;
            case "DateTimeOffset": item.DateTimeOffset = (DateTimeOffset?)value; break;
            case "DateOnly": item.DateOnly = (DateOnly?)value; break;
            case "TimeOnly": item.TimeOnly = (TimeOnly?)value; break;
            case "TimeSpan": item.TimeSpan = (TimeSpan?)value; break;
            case "Level": item.Level = (short?)value; break;
            case "Wide": item.Wide = (ulong?)value; break;
            case "Rights": item.Rights = (int?)value; break;
            case "Exact": item.Exact = (string?)value; break;
            case "Folded": item.Folded = (string?)value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "Item has no nullable field of this name.");
        }

        return item;
    }

    // The value of the field named, as a record holds it.
    public object? ValueOf(string field) => field switch
    {
        "Boolean" => Boolean,
        "Int8" => Int8,
        "UInt8" => UInt8,
        "Int16" => Int16,
        "UInt16" => UInt16,
        "Int32" => Int32,
        "UInt32" => UInt32,
        "Int64" => Int64,
        "UInt64" => UInt64,
        "Float32" => Float32,
        "Float64" => Float64,
        "Decimal" => Decimal,
        "Guid" => Guid,
        "DateTimeOffset" => DateTimeOffset,
        "DateOnly" => DateOnly,
        "TimeOnly" => TimeOnly,
        "TimeSpan" => TimeSpan,
        "Level" => Level,
        "Wide" => Wide,
        "Rights" => Rights,
        "Exact" => Exact,
        "Folded" => Folded,
        "Count" => Count,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Item has no field of this name."),
    };
}
