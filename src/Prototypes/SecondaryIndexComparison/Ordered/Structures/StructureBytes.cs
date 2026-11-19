namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

// The memory of a candidate: the slots of its live nodes in the arena, header included, and what it
// holds on the managed heap per entry (an Arc node; 0 for the native candidates, whose managed state
// is a constant of the instance), with the shape that explains them.
internal readonly record struct StructureBytes(long NativeBytes, long ManagedBytes, long Nodes, int Height);
