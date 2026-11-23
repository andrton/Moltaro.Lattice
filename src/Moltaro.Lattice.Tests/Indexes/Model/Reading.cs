namespace Moltaro.Lattice.Tests.Indexes.Model;

// Bound to the entity type "Reading" of ModelSchema.Composite, whose index ByKey is (Day, Level, Sensor): a
// DateOnly never null, a nullable Float64 and an Int16 never null.
internal sealed class Reading
{
    public long Id { get; set; }

    public DateOnly Day { get; set; }

    public double? Level { get; set; }

    public short Sensor { get; set; }
}
