using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Binding;

// One schema agreement for the runtime class binder and the compiler's metadata reader.
// The runtime builds conversions from the checked types. The generator visits the same
// checked types without constructing runtime delegates or loading the application's assemblies.
internal static class ClassAgreement
{
    // A public member on the most derived declaring type hides all members of the same
    // name on its bases. The metadata readers supply the accessors seen through overrides.
    public static (TProperty Property, TAccessor Getter, TAccessor Setter) FindProperty<TType, TProperty, TAccessor>(
        TType type, string className, string field, string owner,
        Func<TType, TType?> baseType, Func<TType, string, ClassMember<TProperty, TAccessor>[]> declaredMembers)
        where TType : class
        where TProperty : class
        where TAccessor : class
    {
        for (var declaring = type; declaring is not null; declaring = baseType(declaring))
        {
            var declared = declaredMembers(declaring, field);
            if (declared.Length == 0)
            {
                continue;
            }

            foreach (var member in declared)
            {
                if (member.Property is null)
                {
                    throw Failed($"The public member '{field}' of {className} is a {member.Kind}, not a property; field '{field}' of '{owner}' needs a public property of that name with get and set.");
                }
            }

            var property = declared[0];
            if (declared.Length > 1 || property.IsIndexer)
            {
                throw Failed($"{className} has an indexer named '{field}'; field '{field}' of '{owner}' needs a property of that name.");
            }

            if (property.IsStatic)
            {
                throw Failed($"Property '{field}' of {className} is static; field '{field}' of '{owner}' needs an instance property with get and set.");
            }

            if (!property.HasPublicGet || property.Setter is null)
            {
                throw Failed($"Property '{field}' of {className} has no public get or no set; field '{field}' of '{owner}' needs both.");
            }

            return (property.Property!, property.Getter!, property.Setter);
        }

        throw Failed($"{className} needs a public property '{field}' with get and set, as field '{field}' of '{owner}' declares.");
    }

    public static void CheckConstructor(string className, string role, bool isAbstract, bool hasPublicParameterless)
    {
        if (isAbstract || !hasPublicParameterless)
        {
            throw Failed($"{className}, {role}, needs a public parameterless constructor and cannot be abstract.");
        }
    }

    public static ClassValueTypes<TType> CheckValue<TType>(ClassTypeMetadata<TType> metadata, TType type,
        LatticeValueDeclaration declaration, string className, string field, Func<LatticeException> mismatch)
        where TType : class
    {
        switch (declaration.Type)
        {
            case LatticeFieldTypeEnum.Owned:
                CheckOwnedClass(metadata, type, declaration.OwnedType!, className, field, mismatch);
                return default;

            case LatticeFieldTypeEnum.List:
                if (metadata.ArrayElement(type) is { } element)
                {
                    return new(Element: element, IsArray: true);
                }

                var list = metadata.GenericArguments(type, typeof(List<>)) ?? throw mismatch();
                return new(Element: list[0]);

            case LatticeFieldTypeEnum.Set:
                var set = metadata.GenericArguments(type, typeof(HashSet<>)) ?? throw mismatch();
                return new(Element: set[0]);

            case LatticeFieldTypeEnum.Map:
                var map = metadata.GenericArguments(type, typeof(Dictionary<,>)) ?? throw mismatch();
                return new(Key: map[0], Value: map[1]);

            case LatticeFieldTypeEnum.Enum:
                if (metadata.Matches(type, declaration.PositionType))
                {
                    return default;
                }

                var enumType = declaration.IsNullable ? metadata.GenericArguments(type, typeof(Nullable<>))?[0] : type;
                if (enumType is null || !metadata.IsEnum(enumType))
                {
                    throw mismatch();
                }

                var schemaEnum = declaration.EnumType!;
                var differences = metadata.EnumDifferences(enumType, schemaEnum);
                if (differences.Count != 0)
                {
                    throw Failed(
                        $"Property '{field}' of {className} holds the .NET enum {metadata.Name(enumType)}, which does not agree with enum type '{schemaEnum.Name}' of the schema: {string.Join("; ", differences)}. Change the enum or the schema so that they agree.");
                }

                return new(Enum: enumType);

            default:
                if (!metadata.Matches(type, declaration.PositionType))
                {
                    throw mismatch();
                }

                return default;
        }
    }

    private static void CheckOwnedClass<TType>(ClassTypeMetadata<TType> metadata, TType type, LatticeOwnedType owned,
        string className, string field, Func<LatticeException> mismatch)
        where TType : class
    {
        if (owned.ClrTypeName is null)
        {
            throw Failed(
                $"Property '{field}' of {className} holds values of owned type '{owned.Name}', which names no ClrType: such a value has no form in a class and is accessed through records.");
        }

        if (metadata.Matches(type, typeof(LatticeOwnedValue)))
        {
            throw Failed(
                $"Property '{field}' of {className} holds a LatticeOwnedValue, which only a record holds; in a class a value of owned type '{owned.Name}' is an object of {owned.ClrTypeName}.");
        }

        if (metadata.FullName(type) != owned.ClrTypeName)
        {
            throw mismatch();
        }
    }

    private static LatticeException Failed(string message) => new(LatticeErrorEnum.BindingFailed, message);
}
