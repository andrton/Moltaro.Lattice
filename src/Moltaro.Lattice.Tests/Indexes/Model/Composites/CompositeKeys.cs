namespace Moltaro.Lattice.Tests.Indexes.Model.Composites;

// The composite keys of the model tests, of parts of mixed widths (CompositeSchema):
// - Payment (Guid, String without case, Decimal?), a fixed part after a text part: the Guids differ in
//   their later fields as well, the texts are case pairs and a long text in two spellings, and the
//   Decimals include two scales of one key;
// - Reading (DateOnly, Float64?, Int16): NaN in two forms of bits, both zeros, both infinities, and
//   numbers of both signs;
// - Visit (Boolean?, UInt32, DateTimeOffset?, String?), four parts: the high bit of a UInt32, one instant
//   at two offsets, a text beyond U+FFFF and a long text.
// The ends of a type are among the values a part never stores where the part takes values short of them,
// so that a range has bounds beyond what is stored.
internal static class CompositeKeys
{
    public static CompositeKey<Payment> Payment { get; } = new()
    {
        Table = new ModelTable<Payment>
        {
            EntityType = "Payment",
            Index = "ByKey",
            Fields = ["Code", "Payee", "Amount"],
            Model = new IndexModel(IndexModel.ByValue, IndexModel.WithoutCase, IndexModel.ByValue),
            ToEntity = entry => new Payment { Id = entry.Id, Code = (Guid)entry.Key[0]!, Payee = (string)entry.Key[1]!, Amount = (decimal?)entry.Key[2] },
            FromEntity = payment => new ModelEntry(payment.Id, [payment.Code, payment.Payee, payment.Amount]),
            Equal = values => values.Count switch
            {
                0 => IndexKey.All,
                1 => IndexKey.Equal((Guid)values[0]!),
                2 => IndexKey.Equal((Guid)values[0]!, (string)values[1]!),
                _ => IndexKey.Equal((Guid)values[0]!, (string)values[1]!, (decimal?)values[2]),
            },
        },
        Values =
        [
            [
                Guid.Empty, Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("12345678-9abc-def0-0000-000000000000"),
                Guid.Parse("12345678-9abc-def0-8000-000000000000"), Guid.Parse("12345678-9abc-def0-ffff-ffffffffffff"), Guid.AllBitsSet,
            ],
            ["", "anna", "ANNA", "Anna", "bob", "BOB", "é", "É", "ß", "ẞ", new string('x', 70) + "1", new string('X', 70) + "1"],
            [null, -1.5m, 0m, 0.00m, 1m, 1.000m, 12_345.6789m],
        ],
        NeverStored =
        [
            [Guid.Parse("12345678-9abc-def0-7fff-ffffffffffff")],
            ["m", "\U0000FFFF"],
            [decimal.MinValue, 0.5m, decimal.MaxValue],
        ],
    };

    public static CompositeKey<Reading> Reading { get; } = new()
    {
        Table = new ModelTable<Reading>
        {
            EntityType = "Reading",
            Index = "ByKey",
            Fields = ["Day", "Level", "Sensor"],
            Model = new IndexModel(IndexModel.ByValue, IndexModel.ByValue, IndexModel.ByValue),
            ToEntity = entry => new Reading { Id = entry.Id, Day = (DateOnly)entry.Key[0]!, Level = (double?)entry.Key[1], Sensor = (short)entry.Key[2]! },
            FromEntity = reading => new ModelEntry(reading.Id, [reading.Day, reading.Level, reading.Sensor]),
            Equal = values => values.Count switch
            {
                0 => IndexKey.All,
                1 => IndexKey.Equal((DateOnly)values[0]!),
                2 => IndexKey.Equal((DateOnly)values[0]!, (double?)values[1]),
                _ => IndexKey.Equal((DateOnly)values[0]!, (double?)values[1], (short)values[2]!),
            },
        },
        Values =
        [
            [new DateOnly(1, 1, 2), new DateOnly(1969, 12, 31), new DateOnly(1970, 1, 1), new DateOnly(2026, 11, 23), new DateOnly(9999, 12, 30)],
            [null, double.NaN, -double.NaN, double.NegativeInfinity, -1.5, -0d, 0d, 1.5, double.PositiveInfinity],
            [(short)-32_767, (short)-1, (short)0, (short)1, (short)256, (short)32_766],
        ],
        NeverStored =
        [
            [DateOnly.MinValue, new DateOnly(2000, 1, 1), DateOnly.MaxValue],
            [0.75],
            [short.MinValue, (short)100, short.MaxValue],
        ],
    };

    public static CompositeKey<Visit> Visit { get; } = new()
    {
        Table = new ModelTable<Visit>
        {
            EntityType = "Visit",
            Index = "ByKey",
            Fields = ["Confirmed", "Room", "At", "Note"],
            Model = new IndexModel(IndexModel.ByValue, IndexModel.ByValue, IndexModel.ByValue, IndexModel.Ordinal),
            ToEntity = entry => new Visit { Id = entry.Id, Confirmed = (bool?)entry.Key[0], Room = (uint)entry.Key[1]!, At = (DateTimeOffset?)entry.Key[2], Note = (string?)entry.Key[3] },
            FromEntity = visit => new ModelEntry(visit.Id, [visit.Confirmed, visit.Room, visit.At, visit.Note]),
            Equal = values => values.Count switch
            {
                0 => IndexKey.All,
                1 => IndexKey.Equal((bool?)values[0]),
                2 => IndexKey.Equal((bool?)values[0], (uint)values[1]!),
                3 => IndexKey.Equal((bool?)values[0], (uint)values[1]!, (DateTimeOffset?)values[2]),
                _ => IndexKey.Equal((bool?)values[0], (uint)values[1]!, (DateTimeOffset?)values[2], (string?)values[3]),
            },
        },
        Values =
        [
            [null, false, true],
            [1u, 0x7fff_ffffu, 0x8000_0000u, 0xffff_fffeu],
            [
                null, DateTimeOffset.UnixEpoch, new DateTimeOffset(1970, 1, 1, 5, 30, 0, TimeSpan.FromMinutes(330)),
                new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.FromHours(3)), new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ],
            [null, "", "a", "A", "\U0000E000", "\U0001F600", new string('x', 300)],
        ],
        NeverStored =
        [
            [],
            [0u, 1_000u, uint.MaxValue],
            [DateTimeOffset.MinValue, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)), DateTimeOffset.MaxValue],
            ["m", "\U0000FFFF"],
        ],
    };

    // Runs the cell of the composite key named, in a database of the string encoding given.
    public static Task RunAsync(string name, string encoding) => name switch
    {
        "Payment" => Payment.RunAsync(encoding),
        "Reading" => Reading.RunAsync(encoding),
        _ => Visit.RunAsync(encoding),
    };
}
