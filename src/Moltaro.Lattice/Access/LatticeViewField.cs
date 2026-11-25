using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>A generated view's requirement for one field of the schema.</summary>
/// <param name="Path">The case-sensitive field path, with a dot between fields of an owned value.</param>
/// <param name="RequiredKind">The required field kind, when the projection depends on it.</param>
/// <param name="RequiredNullable">The required nullability, when the projection depends on it.</param>
/// <remarks>A path can cross an owned value. It ends at a List, Map or Set field.</remarks>
public readonly record struct LatticeViewField(string Path, LatticeFieldTypeEnum? RequiredKind = null, bool? RequiredNullable = null);
