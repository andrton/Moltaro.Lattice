namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Scalars;

// The temporal types read back as written, at full tick resolution, from their minimum to their
// maximum (brief of S05, Acceptance; Q03.4.1, "UTC timestamps" and "DateOnly, TimeOnly and
// TimeSpan"). A DateTimeOffset is an instant: it is stored as its UTC ticks and read back with
// offset zero, so a value given with another offset reads back as the same instant in UTC and its
// offset is not kept. A DateOnly is a date, a TimeOnly a time of day, a TimeSpan a signed duration;
// none of them is converted.
public sealed class TemporalRoundTripTests
{
    [Fact]
    public void A_DateTimeOffset_reads_back_as_the_same_instant_with_offset_zero()
    {
        DateTimeOffset[] values =
        [
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            DateTimeOffset.UnixEpoch,
            new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567),
            new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 11, 13, 1, 30, 0, TimeSpan.FromHours(-12)),
            new DateTimeOffset(2026, 11, 13, 23, 45, 0, TimeSpan.FromMinutes(14 * 60)),
            new DateTimeOffset(2026, 11, 13, 5, 0, 0, TimeSpan.FromMinutes(-150)),
            new DateTimeOffset(1, 1, 1, 14, 0, 0, TimeSpan.FromHours(14)),
            new DateTimeOffset(9999, 12, 31, 10, 0, 0, TimeSpan.FromHours(-12)),
        ];
        using var item = TypedItem.OfType("DateTimeOffset");

        foreach (var value in values)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetDateTimeOffset(item.Field("Value"), value);
            writer.SetDateTimeOffset(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            foreach (var read in new[] { reader.GetDateTimeOffset(item.Field("Value")), reader.GetDateTimeOffset(item.Field("Maybe")) })
            {
                Assert.Equal(value.UtcTicks, read.UtcTicks);
                Assert.Equal(TimeSpan.Zero, read.Offset);
            }
        }
    }

    [Fact]
    public void Two_offsets_of_the_same_instant_read_back_as_the_same_value()
    {
        // The example of Q03.4.1.
        var local = new DateTimeOffset(2026, 11, 13, 15, 0, 0, TimeSpan.FromHours(3));
        var utc = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        using var item = TypedItem.OfType("DateTimeOffset");

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetDateTimeOffset(item.Field("Value"), local);
        writer.SetDateTimeOffset(item.Field("Maybe"), utc);
        var reader = item.Reader(writer.Complete());

        // Equal as instants is not enough: EqualsExact compares the offset as well.
        Assert.True(utc.EqualsExact(reader.GetDateTimeOffset(item.Field("Value"))));
        Assert.True(utc.EqualsExact(reader.GetDateTimeOffset(item.Field("Maybe"))));
    }

    [Fact]
    public void A_DateOnly_reads_back_as_written()
    {
        DateOnly[] values = [DateOnly.MinValue, DateOnly.MaxValue, new(1970, 1, 1), new(2024, 2, 29), new(2026, 11, 15)];
        using var item = TypedItem.OfType("DateOnly");

        foreach (var value in values)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetDateOnly(item.Field("Value"), value);
            writer.SetDateOnly(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value, reader.GetDateOnly(item.Field("Value")));
            Assert.Equal(value, reader.GetDateOnly(item.Field("Maybe")));
        }
    }

    [Fact]
    public void A_TimeOnly_reads_back_as_written_to_the_tick()
    {
        TimeOnly[] values = [TimeOnly.MinValue, TimeOnly.MaxValue, new(1), new(12, 0), new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(1))];
        using var item = TypedItem.OfType("TimeOnly");

        foreach (var value in values)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetTimeOnly(item.Field("Value"), value);
            writer.SetTimeOnly(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value.Ticks, reader.GetTimeOnly(item.Field("Value")).Ticks);
            Assert.Equal(value.Ticks, reader.GetTimeOnly(item.Field("Maybe")).Ticks);
        }
    }

    [Fact]
    public void A_TimeSpan_reads_back_as_written_negative_durations_included()
    {
        TimeSpan[] values =
        [
            TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromTicks(-1),
            TimeSpan.FromDays(-1), TimeSpan.FromDays(400).Add(TimeSpan.FromTicks(7)),
        ];
        using var item = TypedItem.OfType("TimeSpan");

        foreach (var value in values)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetTimeSpan(item.Field("Value"), value);
            writer.SetTimeSpan(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value, reader.GetTimeSpan(item.Field("Value")));
            Assert.Equal(value, reader.GetTimeSpan(item.Field("Maybe")));
        }
    }
}
