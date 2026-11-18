using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// 32 bytes inline in a struct, for a copied key of 32 bytes (CopyEntry).
[InlineArray(32)]
internal struct Bytes32
{
    private byte _first;
}
