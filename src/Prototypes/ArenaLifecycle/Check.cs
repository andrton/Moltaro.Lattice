namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
