using System.Linq.Expressions;
using System.Runtime.CompilerServices;
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

    // Null for a field that holds an owned value or a collection: reading such a field into an
    // object comes with storing it, until then no entity of such a type is stored.
    private readonly Action<TEntity, object?>?[] _setters;

    private EntityBinding(LatticeEntityType entityType, Func<TEntity> create, BoundProperty[] properties, Action<TEntity, object?>?[] setters)
    {
        EntityType = entityType;
        _create = create;
        _properties = properties;
        _setters = setters;
    }

    public LatticeEntityType EntityType { get; }

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
        var setters = properties.Select(Setter).ToArray();

        var create = Expression.Lambda<Func<TEntity>>(Expression.New(constructor)).Compile();
        return new EntityBinding<TEntity>(entityType, create, properties, setters);
    }

    // Entity -> values to store, in the form a record holds and checked against the schema at
    // every depth. The result shares nothing mutable with the entity.
    public object?[] Capture(TEntity entity)
    {
        var values = new object?[_properties.Length];
        foreach (var property in _properties)
        {
            var value = property.Get(entity);
            if (value is not null)
            {
                values[property.Field.Ordinal] = property.ToRecord(value);
            }
            else if (!property.Field.Declaration.IsNullable)
            {
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"{EntityType.Name}.{property.Field.Name} is required and has no value.");
            }
        }

        return values;
    }

    // Stored values -> a new entity owned by the caller.
    public TEntity Materialize(object?[] values)
    {
        var entity = _create();
        for (var index = 0; index < values.Length; index++)
        {
            var setter = _setters[index]
                ?? throw new NotSupportedException($"Reading owned values and collections into an object is not implemented yet: '{EntityType.Name}.{_properties[index].Field.Name}' holds one.");

            var value = values[index];
            setter(entity, value is byte[] bytes ? bytes.Clone() : value);
        }

        return entity;
    }

    // Writes a stored value of a field that is not an owned value or a collection to its
    // property: as it is, or converted to the .NET enum that the property is declared as.
    private static Action<TEntity, object?>? Setter(BoundProperty property)
    {
        var declaration = property.Field.Declaration;
        if (FieldTypes.IsNested(declaration.Type))
        {
            return null;
        }

        var entity = Expression.Parameter(typeof(TEntity), "entity");
        var value = Expression.Parameter(typeof(object), "value");
        var stored = Expression.Convert(value, declaration.PositionType);
        var member = Expression.Property(entity, property.Property);

        return Expression
            .Lambda<Action<TEntity, object?>>(Expression.Assign(member, Expression.Convert(stored, property.Property.PropertyType)), entity, value)
            .Compile();
    }
}
