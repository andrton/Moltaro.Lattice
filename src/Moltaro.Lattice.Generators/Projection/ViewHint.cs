using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Generators.Projection;

internal readonly record struct ViewHint(LatticeFieldTypeEnum? Kind, bool? IsNullable, bool Ignore);
