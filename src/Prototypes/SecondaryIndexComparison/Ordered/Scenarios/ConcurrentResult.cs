namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// What a concurrent run did: the operations of all readers, what they counted besides (entries found,
// restarts), the writer's operations, and the seconds it ran.
internal readonly record struct ConcurrentResult(long Reads, long Extra, long Writes, double Seconds);
