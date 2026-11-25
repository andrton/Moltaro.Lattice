using Microsoft.CodeAnalysis;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Generators.Projection;

internal sealed record ViewField(string Name, string Path, ITypeSymbol ClrType, LatticeFieldTypeEnum Kind, bool IsNullable,
    LatticeFieldTypeEnum? RequiredKind, bool? RequiredNullable, ViewField[] Children)
{
    public int FieldCount { get; } = 1 + Children.Sum(child => child.FieldCount);
}
