using System.Buffers;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// The copy of one root as an object of a bound class, for one read (ClassCopier). Its scratch is
// rented from the shared pools when the root is copied and given back by Return: an array of bytes for
// the fixed part and an array of the String and Binary values, which is cleared when it is given
// back, so that the pool keeps nothing of the entity. A pool gives an array it holds without
// allocating, so a read allocates the returned object, its strings and arrays, and nothing else.
internal struct ClassCopy<TEntity> : IRootCopy<TEntity>
    where TEntity : class
{
    private readonly ClassCopier<TEntity> _copier;

    private byte[]? _fixedPart;

    private object?[]? _values;

    public ClassCopy(ClassCopier<TEntity> copier)
    {
        _copier = copier;
    }

    public void Copy(in RootReader reader)
    {
        _fixedPart = ArrayPool<byte>.Shared.Rent(_copier.FixedPartLength);
        _values = _copier.ValueCount == 0 ? [] : ArrayPool<object?>.Shared.Rent(_copier.ValueCount);
        _copier.Copy(reader, _fixedPart, _values);
    }

    public readonly TEntity Build() => _copier.Build(_fixedPart.AsSpan(0, _copier.FixedPartLength), _values!);

    public void Return()
    {
        if (_fixedPart is not null)
        {
            ArrayPool<byte>.Shared.Return(_fixedPart);
            _fixedPart = null;
        }

        if (_values is { Length: > 0 })
        {
            ArrayPool<object?>.Shared.Return(_values, clearArray: true);
        }

        _values = null;
    }
}
