using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

// The link between a .NET class and its entity type. The class is inspected once, when the
// binding is created: every schema field gets a compiled getter and setter. Reads and
// writes afterwards use only those delegates.
internal sealed class EntityBinding<TEntity>
    where TEntity : class
{
    // One binding per schema and class, shared by every database that uses the schema.
    private static readonly ConditionalWeakTable<LatticeSchema, EntityBinding<TEntity>> Cache = [];

    private readonly Func<TEntity> _create;
    private readonly Func<TEntity, object?>[] _getters;
    private readonly Action<TEntity, object?>[] _setters;

    private EntityBinding(LatticeEntityType entityType, Func<TEntity> create, Func<TEntity, object?>[] getters, Action<TEntity, object?>[] setters)
    {
        EntityType = entityType;
        _create = create;
        _getters = getters;
        _setters = setters;
    }

    public LatticeEntityType EntityType { get; }

    public static EntityBinding<TEntity> For(LatticeSchema schema) => Cache.GetValue(schema, Create);

    private static EntityBinding<TEntity> Create(LatticeSchema schema)
    {
        var type = typeof(TEntity);
        if (!schema.TryGetEntityType(type, out var entityType))
        {
            throw BindingFailed($"The schema declares no entity type for the .NET class {type.FullName}. Declare it with \"ClrType\": \"{type.FullName}\", or access the entity type by name.");
        }

        if (type.GetConstructor(Type.EmptyTypes) is not { } constructor)
        {
            throw BindingFailed($"{type.Name} needs a public parameterless constructor.");
        }

        var getters = new Func<TEntity, object?>[entityType.Fields.Count];
        var setters = new Action<TEntity, object?>[entityType.Fields.Count];
        var entity = Expression.Parameter(type, "entity");
        var value = Expression.Parameter(typeof(object), "value");

        foreach (var field in entityType.Fields)
        {
            var expected = field.Declaration.IsNullable && field.Declaration.ValueType.IsValueType
                ? typeof(Nullable<>).MakeGenericType(field.Declaration.ValueType)
                : field.Declaration.ValueType;

            var property = type.GetProperty(field.Name);
            if (property is null || property.PropertyType != expected || property.GetGetMethod() is null || property.SetMethod is null)
            {
                var nullable = field.Declaration.IsNullable ? "nullable " : "";
                throw BindingFailed($"{type.Name} needs a public {nullable}{field.Declaration.ValueType.Name} property '{field.Name}' with get and set, as entity type '{entityType.Name}' declares.");
            }

            var member = Expression.Property(entity, property);
            getters[field.Ordinal] = Expression
                .Lambda<Func<TEntity, object?>>(Expression.Convert(member, typeof(object)), entity)
                .Compile();
            setters[field.Ordinal] = Expression
                .Lambda<Action<TEntity, object?>>(Expression.Assign(member, Expression.Convert(value, expected)), entity, value)
                .Compile();
        }

        var create = Expression.Lambda<Func<TEntity>>(Expression.New(constructor)).Compile();
        return new EntityBinding<TEntity>(entityType, create, getters, setters);
    }

    // Entity -> values to store. The result shares nothing mutable with the entity.
    public object?[] Capture(TEntity entity)
    {
        var values = new object?[_getters.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var value = _getters[index](entity);
            values[index] = value is byte[] bytes ? bytes.Clone() : value;
        }

        FieldValues.EnsureRequiredValues(EntityType, values);
        return values;
    }

    // Stored values -> a new entity owned by the caller.
    public TEntity Materialize(object?[] values)
    {
        var entity = _create();
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            _setters[index](entity, value is byte[] bytes ? bytes.Clone() : value);
        }

        return entity;
    }

    private static LatticeException BindingFailed(string message) => new(LatticeErrorEnum.BindingFailed, message);
}
