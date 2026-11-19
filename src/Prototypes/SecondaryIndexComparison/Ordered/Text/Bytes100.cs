using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// 100 bytes inline in a struct, for a copied key of 100 bytes (CopyEntry).
[InlineArray(100)]
internal struct Bytes100
{
    private byte _first;
}
