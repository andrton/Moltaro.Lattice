namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    public static void Rejects(Action operation, string message)
    {
        try { operation(); }
        catch (ProtocolException) { return; }
        throw new InvalidOperationException(message);
    }

    public static bool Same(IReadOnlyDictionary<RootKey, DocumentValue> first,
        IReadOnlyDictionary<RootKey, DocumentValue> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var other)
            && pair.Value.Number == other.Number && pair.Value.Items.SequenceEqual(other.Items));
}
