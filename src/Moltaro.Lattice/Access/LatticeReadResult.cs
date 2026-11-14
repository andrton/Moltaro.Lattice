using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Access;

/// <summary>
/// The result of reading one entity: the entity when it exists, and the position the
/// answer is based on. "Not found" is a result too and has a position.
/// </summary>
/// <typeparam name="TEntity">The entity class, or <see cref="LatticeRecord"/>.</typeparam>
public readonly struct LatticeReadResult<TEntity>
    where TEntity : class
{
    internal LatticeReadResult(TEntity? entity, LatticeRevision revision, LatticePosition position)
    {
        Entity = entity;
        Revision = revision;
        Position = position;
    }

    /// <summary>Whether the entity exists.</summary>
    [MemberNotNullWhen(true, nameof(Entity))]
    public bool Found => Entity is not null;

    /// <summary>An owned copy of the entity; <see langword="null"/> when it does not exist.</summary>
    public TEntity? Entity { get; }

    /// <summary>The revision of exactly the returned state, when the entity exists.</summary>
    public LatticeRevision Revision { get; }

    /// <summary>
    /// The position this answer is based on: the node had applied everything up to it when
    /// it read the entity.
    /// </summary>
    public LatticePosition Position { get; }
}
