using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Maintenance;

internal static class CompoundRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source) =>
        throw new NotSupportedException("Moving a raw directory or chunk is not implemented yet.");
}
