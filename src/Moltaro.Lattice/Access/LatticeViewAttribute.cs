namespace Moltaro.Lattice.Access;

/// <summary>Requests a generated protected view for a .NET class.</summary>
/// <remarks>Apply this attribute to a <c>readonly ref partial struct</c>.</remarks>
/// <param name="entityClass">The class of the entity type to view.</param>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class LatticeViewAttribute(Type entityClass) : Attribute
{
    /// <summary>The .NET class of the entity type to view.</summary>
    public Type EntityClass { get; } = entityClass ?? throw new ArgumentNullException(nameof(entityClass));
}
