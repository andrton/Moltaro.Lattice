using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>A generated protected projection for the .NET class of an entity type.</summary>
/// <remarks>This interface is implemented by the source generator.</remarks>
/// <typeparam name="TSelf">The generated view.</typeparam>
/// <typeparam name="TEntity">The .NET class of the entity type.</typeparam>
public interface ILatticeEntityView<TSelf, TEntity>
    where TSelf : ILatticeEntityView<TSelf, TEntity>, allows ref struct
    where TEntity : class
{
    /// <summary>The field paths and kinds the view needs, in the order used by the projection.</summary>
    static abstract ReadOnlySpan<LatticeViewField> Fields { get; }

    /// <summary>Projects the protected record with the handles already bound to its schema.</summary>
    /// <param name="record">The record state borrowed for one synchronous viewer.</param>
    /// <param name="fields">The bound handles in the order defined by <see cref="Fields"/>.</param>
    /// <returns>A view valid for the lifetime of the borrowed record.</returns>
    static abstract TSelf Create(LatticeRecordView record, ReadOnlySpan<LatticeField> fields);
}
