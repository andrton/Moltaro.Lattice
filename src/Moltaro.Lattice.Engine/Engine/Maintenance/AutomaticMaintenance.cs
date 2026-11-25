namespace Moltaro.Lattice.Engine.Maintenance;

// The work counts of one automatic turn. Selection counts both sides of a move.
internal static class AutomaticMaintenance
{
    public const int SelectionWorkLimit = 128;

    public const int ReclamationWorkLimit = 64;
}
