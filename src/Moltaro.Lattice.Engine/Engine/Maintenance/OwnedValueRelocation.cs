using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Maintenance;

internal static class OwnedValueRelocation
{
    public static bool TryMove(DatabaseParts parts, RootHandle expected, ArenaAddress source) =>
        throw new NotSupportedException("Moving an owned native value is not implemented yet.");
}
