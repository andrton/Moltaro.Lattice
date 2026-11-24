using System.Buffers;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Sets;

// The copy of one root as an object of a bound class, for one read (ClassCopier). Its scratch is
// rented from the shared pools when the root is copied and given back by Return: an array of bytes for
// the fixed part and an array of the values that lie outside it or beneath it. A pool gives an array it
// holds without allocating, so a read of an entity type without owned values and Lists allocates the
// returned object, its strings and arrays, and nothing else; one with them allocates besides the form a
// record holds of each, from which the objects are made.
//
// What the pools keep of the entity. The array of the values is cleared when it goes back: it
// references the strings and arrays the read returned, up to 268,173,376 bytes each, and the owned
// values and Lists it made, and the pool would keep them alive until the array is used again. The
// array of the fixed part goes back as it is: its bytes, the scalars of the entity, stay in it until
// its next user overwrites them, but they reference nothing and keep no memory alive, and the pool is
// the process's own, so clearing them would protect nothing.
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
