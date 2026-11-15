using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// Makes the objects of a bound class that reads return: a new object by the public parameterless
// constructor, every field set from the root through a compiled setter (ClassField). Made once for
// a set of an entity type the engine stores, from the binding of the core, which found the property
// of every field by the rules of ILatticeDatabase.Set<TEntity>().
internal sealed class ClassCopier<TEntity>
    where TEntity : class
{
    private readonly EntityBinding<TEntity> _binding;

    // By LatticeField.Ordinal, as the binding has the properties.
    private readonly ClassField<TEntity>[] _fields;

    public ClassCopier(EntityBinding<TEntity> binding)
    {
        _binding = binding;
        _fields = [.. binding.Properties.Select(ClassField<TEntity>.Compile)];
    }

    // A new object; its constructor is the application's code and runs under no gate.
    public TEntity Create() => _binding.CreateObject();

    // Sets every field of the object from the root. The read holds the protection of the root, as
    // Q06 has it fill a result under protection, so the set accessors of the class, which are the
    // application's code, run while the root's stripe is held shared: one that reads or writes the
    // database itself is not supported, because a gate is not taken twice by one thread.
    public void Fill(TEntity entity, in RootReader reader)
    {
        foreach (ref readonly var field in _fields.AsSpan())
        {
            field.Set(entity, reader);
        }
    }
}
