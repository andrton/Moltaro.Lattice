using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Binding;

// The link between a .NET class and its entity type. The class is bound once, when the binding
// is created, by ClassBinder: every schema field gets the property that holds it, a compiled
// getter and what turns its value into what a record holds, and the classes of the owned
// values it holds are bound with it. Reads and writes afterwards use only those delegates.
internal sealed class EntityBinding<TEntity>
    where TEntity : class
{
    // One binding per schema and class, shared by every database that uses the schema.
    private static readonly ConditionalWeakTable<LatticeSchema, EntityBinding<TEntity>> Cache = [];

    private readonly Func<TEntity> _create;
    private readonly BoundProperty[] _properties;

    private EntityBinding(LatticeEntityType entityType, Func<TEntity> create, BoundProperty[] properties)
    {
        EntityType = entityType;
        _create = create;
        _properties = properties;
    }

    public LatticeEntityType EntityType { get; }

    // The property of every field, by LatticeField.Ordinal, for a reader that sets the stored values
    // without boxing them: the engine, which compiles its own setters from their accessors.
    public IReadOnlyList<BoundProperty> Properties => _properties;

    // A new object of the class, made by its public parameterless constructor.
    public TEntity CreateObject() => _create();

    public static EntityBinding<TEntity> For(LatticeSchema schema) => Cache.GetValue(schema, Create);

    private static EntityBinding<TEntity> Create(LatticeSchema schema)
    {
        var type = typeof(TEntity);
        if (!schema.TryGetEntityType(type, out var entityType))
        {
            throw new LatticeException(
                LatticeErrorEnum.BindingFailed,
                $"The schema declares no entity type for the .NET class {type.FullName}. Declare it with \"ClrType\": \"{type.FullName}\", or access the entity type by name.");
        }

        var constructor = ClassBinder.Constructor(type, $"the class of entity type '{entityType.Name}'");
        var binder = new ClassBinder(entityType);

        // The fields are in the order of their ordinals.
        var properties = entityType.Fields.Select(field => binder.Bind(type, field, entityType.Name)).ToArray();

        var create = Expression.Lambda<Func<TEntity>>(Expression.New(constructor)).Compile();
        return new EntityBinding<TEntity>(entityType, create, properties);
    }

    // Entity -> values to store, in the form a record holds and checked against the schema at
    // every depth. The value of every field of the entity is checked against the bound of one value
    // before it is copied; a String or Binary value inside a collection or an owned value is not, as
    // RecordValues.Submit says why. With copyBinary true the result shares nothing mutable with the
    // entity. With false it shares the byte arrays of the fields of the entity, checked to be byte[]
    // as a copy is: the engine copies each once, into its own memory, while the write runs, and the
    // caller keeps the entity unchanged until the write completes (BoundProperty.Take;
    // RecordValues.Submit says the same of a record).
    public object?[] ValuesForWrite(TEntity entity, bool copyBinary)
    {
        var values = new object?[_properties.Length];
        foreach (var property in _properties)
        {
            var value = property.Get(entity);
            if (value is not null)
            {
                if (!ValueBound.Holds(property.Field, value, EntityType.StringEncoding))
                {
                    throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"{EntityType.Name}.{property.Field.Name} {ValueBound.Problem}");
                }

                values[property.Field.Ordinal] = copyBinary ? property.ToRecord(value) : property.Take(value);
            }
            else if (!property.Field.Declaration.IsNullable)
            {
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"{EntityType.Name}.{property.Field.Name} is required and has no value.");
            }
        }

        return values;
    }
}
