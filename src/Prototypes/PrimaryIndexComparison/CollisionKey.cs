namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

// Deliberately adversarial keys expose deletion maintenance at small sizes.
internal readonly record struct CollisionKey(int Value)
{
    public override int GetHashCode() => 1;
}
