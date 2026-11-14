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
                var orEnum = declaration.EnumType is null ? "" : $", or a {nullable}.NET enum that agrees with enum type '{declaration.EnumType.Name}',";
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
    // the field, once, when the class is bound. They agree as a class agrees with its entity
    // type, or the class does not bind: the same underlying type, [Flags] exactly when the enum
    // type is a Flags type, and the same members, every name with its value, in both
    // directions. Nothing is mapped or guessed, so a schema that changed without the class is
    // found here and not when a value is read. A value with two names therefore has both names
    // in the schema, and the schema says which of them is canonical.
    private static void CheckDotNetEnum(Type type, LatticeField field, Type dotNetEnum)
    {
        var enumType = field.Declaration.EnumType!;
        var stored = field.Declaration.ValueType;
        var differences = new List<string>();

        // Values of another integer type would be read as other values, so the members are
        // not compared then.
        var underlyingType = Enum.GetUnderlyingType(dotNetEnum);
        if (underlyingType != stored)
        {
            differences.Add($"its underlying type is {underlyingType.Name}, and the enum type is stored as {stored.Name}");
        }
        else
        {
            var declaredValues = enumType.Members.ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
            var namesOfDotNetEnum = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in dotNetEnum.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var value = Int128Of(member.GetRawConstantValue()!);
                namesOfDotNetEnum.Add(member.Name);

                if (!declaredValues.TryGetValue(member.Name, out var declaredValue))
                {
                    differences.Add($"it names {member.Name} = {value}, which the enum type does not declare");
                }
                else if (declaredValue != value)
                {
                    differences.Add($"it names {member.Name} = {value}, which the enum type declares as {declaredValue}");
                }
            }

            foreach (var member in enumType.Members)
            {
                if (!namesOfDotNetEnum.Contains(member.Name))
                {
                    differences.Add($"it has no {member.Name}, which the enum type declares as {member.Value}");
                }
            }
        }

        var isFlags = dotNetEnum.IsDefined(typeof(FlagsAttribute), inherit: false);
        if (isFlags != enumType.IsFlags)
        {
            differences.Add(enumType.IsFlags
                ? "it has no [Flags], and the enum type is a Flags type"
                : "it has [Flags], and the enum type is not a Flags type");
        }

        if (differences.Count > 0)
        {
            throw BindingFailed(
                $"Property '{field.Name}' of {type.Name} is the .NET enum {dotNetEnum.Name}, which does not agree with enum type '{enumType.Name}' of the schema: {string.Join("; ", differences)}. Change the enum or the schema so that they agree.");
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
