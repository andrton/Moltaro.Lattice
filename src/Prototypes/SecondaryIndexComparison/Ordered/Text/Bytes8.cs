using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// 8 bytes inline in a struct, for a copied key of 8 bytes (CopyEntry).
[InlineArray(8)]
internal struct Bytes8
{
    private byte _first;
}
