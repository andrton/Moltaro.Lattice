using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Lifetime;

// A layout version that is not the one of the layout, for the tests that state that a root of another
// version is refused: none, the one below the layout's, the one above it, and the largest. They are taken
// from the version the engine states (RootLayout.LayoutVersion), so that they stay other versions when
// the engine raises its own.
internal static class OtherLayoutVersion
{
    public static uint Of(RootLayout layout, string version) => version switch
    {
        "none" => 0,
        "an older one" => layout.LayoutVersion - 1,
        "a newer one" => layout.LayoutVersion + 1,
        _ => uint.MaxValue,
    };
}
