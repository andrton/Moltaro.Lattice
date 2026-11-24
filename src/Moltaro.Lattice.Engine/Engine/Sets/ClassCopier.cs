using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Sets;

// Makes the objects of a bound class that reads return, in the two steps of a read (IRootCopy). Under
// the protection of the root, Copy copies the fixed part of the root block and makes the strings and
// arrays of its String and Binary values, and every owned value and List in the form a record holds it,
// a LatticeOwnedValue or a List<T> with all they hold, into scratch the read owns; it runs no code of the
// application, since only the types of the core and of .NET are made there. After the read released the
// protection, Build makes a new object by the public parameterless constructor and sets every field from
// the scratch through a compiled setter (ClassField), which turns an owned value or a List into the
// objects of the classes of the application first (BoundProperty.FromRecord): the constructors and the
// set accessors are the application's code and run outside the protection of reads (the planner's word
// on the first review of S06). Made once for a set of an entity type the engine stores, from the binding
// of the core, which found the property of every field by the rules of ILatticeDatabase.Set<TEntity>().
internal sealed class ClassCopier<TEntity>
    where TEntity : class
{
    private readonly EntityBinding<TEntity> _binding;

    private readonly RootLayout _layout;

    // The fields that are not nested, by LatticeField.Ordinal as the binding has the properties.
    private readonly ClassField<TEntity>[] _fields;

    // The nested fields, the owned values and Lists of the entity, in the same order: copied and set in a
    // pass of their own (ClassField); empty for an entity type without them.
    private readonly ClassField<TEntity>[] _nested;

    public ClassCopier(EntityBinding<TEntity> binding, RootLayout layout)
    {
        _binding = binding;
        _layout = layout;

        var fields = new List<ClassField<TEntity>>();
        var nested = new List<ClassField<TEntity>>();
        var slots = 0;

        foreach (var property in binding.Properties)
        {
            if (ClassField<TEntity>.IsNested(property.Field))
            {
                nested.Add(ClassField<TEntity>.Compile(property, slots++));
            }
            else
            {
                var slot = ClassField<TEntity>.HasSlot(property.Field) ? slots++ : ClassField<TEntity>.NoSlot;

                fields.Add(ClassField<TEntity>.Compile(property, slot));
            }
        }

        _fields = [.. fields];
        _nested = [.. nested];
        ValueCount = slots;
    }

    // The bytes of the copy of the fixed part of a root.
    public int FixedPartLength => RootReader.FixedPartLength(_layout);

    // The slots of the values of String and Binary fields and of nested fields.
    public int ValueCount { get; }

    // Under the protection of the root: copies its fixed part into fixedPart and makes its String and
    // Binary values, and its nested fields in the form a record holds them, into their slots.
    public void Copy(in RootReader root, Span<byte> fixedPart, object?[] values)
    {
        root.CopyFixedPart(fixedPart);

        foreach (ref readonly var field in _fields.AsSpan())
        {
            if (field.Slot != ClassField<TEntity>.NoSlot)
            {
                field.CopyValue(root, values);
            }
        }

        foreach (ref readonly var field in _nested.AsSpan())
        {
            field.CopyNested(root, values);
        }
    }

    // After the protection: a new object with the values of the copy.
    public TEntity Build(ReadOnlySpan<byte> fixedPart, object?[] values)
    {
        var entity = _binding.CreateObject();
        var copied = new RootReader(_layout, fixedPart);

        foreach (ref readonly var field in _fields.AsSpan())
        {
            field.Set(entity, copied, values);
        }

        foreach (ref readonly var field in _nested.AsSpan())
        {
            field.SetNested(entity, values);
        }

        return entity;
    }
}
