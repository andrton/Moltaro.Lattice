namespace Moltaro.Lattice.Tests.NestedValues.Collections;

internal sealed class SetRepresentations
{
    public long Id { get; set; }

    public HashSet<string> Names { get; set; } = [];

    public HashSet<decimal> Amounts { get; set; } = [];

    public HashSet<double> Numbers { get; set; } = [];

    public HashSet<DateTimeOffset> Instants { get; set; } = [];

    public HashSet<string> References { get; set; } = [];

    public HashSet<int?> Optional { get; set; } = [];
}
