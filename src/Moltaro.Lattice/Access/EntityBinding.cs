using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

// The link between a .NET class and its entity type. The class is inspected once, when the
// binding is created: every schema field gets a compiled getter and setter, and a .NET enum
// property is checked against the enum type of its field. Reads and writes afterwards use
// only those delegates.
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
            var declaration = field.Declaration;

            // What the database holds for the field, and what a record holds.
            var stored = declaration.IsNullable && declaration.ValueType.IsValueType
                ? typeof(Nullable<>).MakeGenericType(declaration.ValueType)
                : declaration.ValueType;

            var property = type.GetProperty(field.Name);
            var dotNetEnum = property is null ? null : DotNetEnumOf(property.PropertyType, declaration);
            if (property is null || (property.PropertyType != stored && dotNetEnum is null) || property.GetGetMethod() is null || property.SetMethod is null)
            {
                var nullable = declaration.IsNullable ? "nullable " : "";
                var orEnum = declaration.EnumType is null ? "" : $", or a {nullable}.NET enum of that underlying type,";
                throw BindingFailed($"{type.Name} needs a public {nullable}{declaration.ValueType.Name} property '{field.Name}'{orEnum} with get and set, as entity type '{entityType.Name}' declares.");
            }

            var member = Expression.Property(entity, property);
            Expression read = member;
            Expression write = Expression.Convert(value, stored);

            if (dotNetEnum is not null)
            {
                CheckDotNetEnum(type, field, dotNetEnum);

                // The property is a .NET enum; the database holds its underlying integer.
                read = Expression.Convert(member, stored);
                write = Expression.Convert(write, property.PropertyType);
            }

            getters[field.Ordinal] = Expression
                .Lambda<Func<TEntity, object?>>(Expression.Convert(read, typeof(object)), entity)
                .Compile();
            setters[field.Ordinal] = Expression
                .Lambda<Action<TEntity, object?>>(Expression.Assign(member, write), entity, value)
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

    // The .NET enum a property of an Enum field is declared as: the enum itself for a field
    // that is not nullable, Nullable<> of it for one that is. Null for any other property type.
    private static Type? DotNetEnumOf(Type propertyType, LatticeValueDeclaration declaration)
    {
        if (declaration.EnumType is null)
        {
            return null;
        }

        var candidate = declaration.IsNullable ? Nullable.GetUnderlyingType(propertyType) : propertyType;
        return candidate is { IsEnum: true } ? candidate : null;
    }

    // The one place where a .NET enum is compared with the enum type the schema declares for
    // the field, once, when the class is bound. It is stored as the same integer type, or its
    // values would be read as another one. A .NET enum with two names for one value binds only
    // when the schema declares that value: the schema then names it canonically, and nothing
    // has to choose between the names of the .NET enum.
    private static void CheckDotNetEnum(Type type, LatticeField field, Type dotNetEnum)
    {
        var enumType = field.Declaration.EnumType!;
        var underlyingType = Enum.GetUnderlyingType(dotNetEnum);
        if (underlyingType != field.Declaration.ValueType)
        {
            throw BindingFailed(
                $"{type.Name}.{field.Name} is the .NET enum {dotNetEnum.Name}, of the underlying type {underlyingType.Name}; enum type '{enumType.Name}' is stored as {field.Declaration.ValueType.Name}.");
        }

        var namesOfValue = new Dictionary<Int128, List<string>>();

        foreach (var member in dotNetEnum.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = Int128Of(member.GetRawConstantValue()!);
            if (!namesOfValue.TryGetValue(value, out var names))
            {
                names = [];
                namesOfValue.Add(value, names);
            }

            names.Add(member.Name);
        }

        foreach (var (value, names) in namesOfValue)
        {
            if (names.Count > 1 && !enumType.Members.Any(member => member.Value == value))
            {
                throw BindingFailed(
                    $"{type.Name}.{field.Name} is the .NET enum {dotNetEnum.Name}, which names {value} {string.Join(" and ", names)}; enum type '{enumType.Name}' does not declare {value}, so it has no canonical name for it.");
            }
        }
    }

    // The value of a member of a .NET enum, in its underlying integer type.
    private static Int128 Int128Of(object value) => value switch
    {
        sbyte number => number,
        byte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number => number,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static LatticeException BindingFailed(string message) => new(LatticeErrorEnum.BindingFailed, message);
}
