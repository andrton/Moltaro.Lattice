using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Maintenance;

internal static class RootRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source) =>
        throw new NotSupportedException("Fixed-root relocation is not implemented yet.");
}
