namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;

// What a database holds at one moment, taken after a drain, so that a test can state that a write which
// had no effect left nothing behind (decision on write admission, Q14: a write refused before its commit
// has had no effect): every counter of the engine, among them the live, prepared and retired bytes of the
// arena, its segments and free regions, the root and text entries, the published sequence and the writes
// queued; every entry of the text table with its references; and every node of every index of the schema
// with its items, in the order of the walk (IndexContents). One figure may differ: the managed capacity of
// the text table, whose bucket table a new entry may have grown before the write was refused, and which
// keeps that room for the next one (Storage.Text.TextTable). That capacity is reported and not charged to
// the memory budget (EngineCounters, TextTableBytes); it never shrinks for the refusal.
internal sealed class HeldState
{
    private readonly EngineCounters _counters;

    private readonly List<string> _texts;

    private readonly List<string> _nodes;

    private HeldState(EngineCounters counters, List<string> texts, List<string> nodes)
    {
        _counters = counters;
        _texts = texts;
        _nodes = nodes;
    }

    // What the database holds now, once a drain has freed what no read holds.
    public static HeldState Of(EngineDatabase database)
    {
        database.DrainRetired();

        return new HeldState(database.Counters, TextsOf(database), NodesOf(database));
    }

    // The database holds again what it held when this was taken.
    public void ExpectHeldBy(EngineDatabase database)
    {
        var now = Of(database);

        Assert.Equal(_counters with { TextTableBytes = now._counters.TextTableBytes }, now._counters);
        Assert.True(now._counters.TextTableBytes >= _counters.TextTableBytes, $"The text table held {_counters.TextTableBytes} managed bytes before and {now._counters.TextTableBytes} after.");
        Assert.Equal(_texts, now._texts);
        Assert.Equal(_nodes, now._nodes);
    }

    // Each entry of the text table as its bytes and its references, in an order of their own.
    private static List<string> TextsOf(EngineDatabase database) =>
        [.. database.TextReferences().Select(entry => $"{Convert.ToHexString(entry.Text)}:{entry.References}").Order(StringComparer.Ordinal)];

    private static List<string> NodesOf(EngineDatabase database) =>
        [.. database.Schema.EntityTypes.SelectMany(type => type.Indexes.SelectMany(index => IndexContents.Of(database, type.Name, index.Name).Walk.Select(node => $"{type.Name}.{index.Name} {node}")))];
}
