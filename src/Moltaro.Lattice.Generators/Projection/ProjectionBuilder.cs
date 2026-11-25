using Microsoft.CodeAnalysis;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Generators.Metadata;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Schema.Compilation;

namespace Moltaro.Lattice.Generators.Projection;

// Builds the projection from a schema file or from the bound properties of the model.
// Collections stop a projection path, although their entire CLR shape still has to agree
// with a supplied schema. Core checks those types using the same rules as the runtime.
internal sealed class ProjectionBuilder(RoslynClassMetadata metadata, Dictionary<string, ViewHint> hints)
{
    private readonly HashSet<ITypeSymbol> _ownedPath = new(SymbolEqualityComparer.Default);

    public ViewField[] FromSchema(INamedTypeSymbol entityClass, LatticeEntityType entity)
    {
        metadata.CheckConstructor(entityClass, $"the class of entity type '{entity.Name}'");
        return SchemaFields(entityClass, entity.Name, entity.Fields, "", project: true);
    }

    public ViewField[] WithoutSchema(INamedTypeSymbol entityClass)
    {
        metadata.CheckConstructor(entityClass, "the entity class of this view");
        _ownedPath.Add(entityClass);
        var fields = ClrFields(entityClass, "");
        if (hints.Count != 0)
        {
            throw Failure(hints.Keys.First(), "no eligible CLR property has this path");
        }

        return fields;
    }

    private ViewField[] SchemaFields(INamedTypeSymbol type, string owner, IReadOnlyList<LatticeField> fields, string prefix, bool project)
    {
        var projected = new List<ViewField>();
        foreach (var field in fields)
        {
            var path = prefix + field.Name;
            if (project && hints.TryGetValue(path, out var hint) && hint.Ignore)
            {
                throw Failure(path, "the supplied schema declares this field and it cannot be ignored");
            }

            var property = metadata.Property(type, field.Name, owner);
            if (property.RefKind != RefKind.None)
            {
                throw Failure(path, "a ref-returning CLR property cannot supply a stored field");
            }

            var declaration = field.Declaration;
            var children = SchemaValue(type, property.Type, declaration, path, project);
            if (project)
            {
                var nullable = HasDistinctNullableForm(declaration.Type, property.Type) ? declaration.IsNullable : (bool?)null;
                projected.Add(new ViewField(field.Name, path, property.Type, declaration.Type, declaration.IsNullable,
                    declaration.Type, nullable, children));
            }
        }

        return [.. projected];
    }

    private ViewField[] SchemaValue(INamedTypeSymbol owner, ITypeSymbol type, LatticeValueDeclaration declaration, string path, bool project)
    {
        var shape = ClassAgreement.CheckValue(metadata, type, declaration, owner.Name, path,
            () => Failure(path, $"CLR type '{type.ToDisplayString()}' does not agree with the schema's {declaration.Type} declaration"));
        switch (declaration.Type)
        {
            case LatticeFieldTypeEnum.Owned:
                var owned = declaration.OwnedType!;
                var ownedClass = (INamedTypeSymbol)type;
                metadata.CheckConstructor(ownedClass, $"the class of owned type '{owned.Name}' at field '{path}'");
                return SchemaFields(ownedClass, owned.Name, owned.Fields, path + ".", project);

            case LatticeFieldTypeEnum.List:
            case LatticeFieldTypeEnum.Set:
                SchemaValue(owner, shape.Element!, declaration.Element!, path, project: false);
                return [];

            case LatticeFieldTypeEnum.Map:
                SchemaValue(owner, shape.Key!, declaration.Key!, path, project: false);
                SchemaValue(owner, shape.Value!, declaration.Value!, path, project: false);
                return [];

            default:
                return [];
        }
    }

    private ViewField[] ClrFields(INamedTypeSymbol type, string prefix)
    {
        var fields = new List<ViewField>();
        foreach (var property in metadata.Properties(type))
        {
            var path = prefix + property.Name;
            hints.Remove(path, out var hint);
            if (hint.Ignore)
            {
                continue;
            }

            metadata.CheckAccessors(property);
            var fieldType = property.Type;
            var kind = ClrKind(fieldType, hint.Kind, path);
            if (hint.Kind is { } selected && selected != kind && !CanProjectAs(kind, selected))
            {
                throw Failure(path, $"CLR type '{fieldType.ToDisplayString()}' does not support {selected}");
            }

            var requiredKind = hint.Kind;
            if (kind is LatticeFieldTypeEnum.Owned or LatticeFieldTypeEnum.Enum or LatticeFieldTypeEnum.List
                or LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set)
            {
                requiredKind ??= kind;
            }

            var nullable = ClrNullability(fieldType, kind, path, hint.IsNullable);
            ViewField[] children = [];
            if (kind == LatticeFieldTypeEnum.Owned)
            {
                // A recursively constructed generic class can change its type arguments
                // at each step, so matching the ancestors alone cannot bound inference.
                if (_ownedPath.Count >= SchemaBuilder.MaximumDepth)
                {
                    throw Failure(path, $"an owned projection cannot exceed the schema depth of {SchemaBuilder.MaximumDepth}");
                }
                var owned = (INamedTypeSymbol)fieldType;
                if (!_ownedPath.Add(owned))
                {
                    throw Failure(path, "an owned value cannot contain its own CLR type");
                }

                metadata.CheckConstructor(owned, $"the class of owned field '{path}'");
                children = ClrFields(owned, path + ".");
                _ownedPath.Remove(owned);
            }

            fields.Add(new ViewField(property.Name, path, fieldType, kind, nullable ?? false, requiredKind, nullable, children));
        }

        return [.. fields];
    }

    private LatticeFieldTypeEnum ClrKind(ITypeSymbol type, LatticeFieldTypeEnum? selected, string path)
    {
        if (metadata.Matches(type, typeof(byte[])))
        {
            return selected switch
            {
                LatticeFieldTypeEnum.Binary => LatticeFieldTypeEnum.Binary,
                LatticeFieldTypeEnum.List => LatticeFieldTypeEnum.List,
                _ => throw Failure(path, "a byte[] needs an explicit Binary or List projection without a schema file"),
            };
        }

        if (metadata.ArrayElement(type) is not null || metadata.GenericArguments(type, typeof(List<>)) is not null)
        {
            return LatticeFieldTypeEnum.List;
        }

        if (metadata.GenericArguments(type, typeof(HashSet<>)) is not null)
        {
            return LatticeFieldTypeEnum.Set;
        }

        if (metadata.GenericArguments(type, typeof(Dictionary<,>)) is not null)
        {
            return LatticeFieldTypeEnum.Map;
        }

        var valueType = metadata.GenericArguments(type, typeof(Nullable<>))?[0] ?? type;
        if (metadata.IsEnum(valueType))
        {
            return LatticeFieldTypeEnum.Enum;
        }

        for (var kind = LatticeFieldTypeEnum.Boolean; kind <= LatticeFieldTypeEnum.TimeSpan; kind++)
        {
            if (metadata.Matches(valueType, FieldTypes.ValueType(kind)))
            {
                return kind;
            }
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Class, SpecialType: SpecialType.None })
        {
            return LatticeFieldTypeEnum.Owned;
        }

        throw Failure(path, $"CLR type '{type.ToDisplayString()}' has no protected field projection");
    }

    private bool? ClrNullability(ITypeSymbol type, LatticeFieldTypeEnum kind, string path, bool? hint)
    {
        if (!HasDistinctNullableForm(kind, type))
        {
            return null;
        }

        if (type.IsValueType)
        {
            return metadata.GenericArguments(type, typeof(Nullable<>)) is not null;
        }

        return hint ?? (type.NullableAnnotation switch
        {
            NullableAnnotation.Annotated => true,
            NullableAnnotation.NotAnnotated => false,
            _ => throw Failure(path, "the CLR property has no nullable annotation; specify IsNullable on the view"),
        });
    }

    private static bool HasDistinctNullableForm(LatticeFieldTypeEnum kind, ITypeSymbol type) =>
        kind != LatticeFieldTypeEnum.Binary && type.SpecialType != SpecialType.System_String;

    private static bool CanProjectAs(LatticeFieldTypeEnum inferred, LatticeFieldTypeEnum selected) =>
        (selected == LatticeFieldTypeEnum.Reference && FieldTypes.CanBeKey(inferred)) ||
        (selected == LatticeFieldTypeEnum.Enum && FieldTypes.IsInteger(inferred));

    internal static LatticeException Failure(string path, string reason) =>
        new(LatticeErrorEnum.BindingFailed, $"Field '{path}' cannot be projected: {reason}.");
}
