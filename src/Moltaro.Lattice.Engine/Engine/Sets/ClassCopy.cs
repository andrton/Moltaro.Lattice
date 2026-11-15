using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// The copy of one root as an object of a bound class, for one read.
internal struct ClassCopy<TEntity> : IRootCopy<TEntity>
    where TEntity : class
{
    private readonly ClassCopier<TEntity> _copier;

    public ClassCopy(ClassCopier<TEntity> copier)
    {
        _copier = copier;
    }

    public TEntity? Result { get; private set; }

    public void Create() => Result = _copier.Create();

    public readonly void Fill(in RootReader reader) => _copier.Fill(Result!, reader);
}
