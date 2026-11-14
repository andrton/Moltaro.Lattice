namespace Moltaro.Lattice.Schema;

// The entity type a Reference refers to: its name, its identity and the declaration of its
// primary key, which is what a reference holds and how it compares. An entity type is built
// after the declarations of its fields, and a field may refer to its own entity type, so a
// declaration refers to its target through this and not through the entity type.
internal sealed record ReferenceTarget(string EntityTypeName, int EntityTypeId, LatticeValueDeclaration Key);
