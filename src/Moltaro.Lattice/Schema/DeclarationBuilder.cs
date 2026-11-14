namespace Moltaro.Lattice.Schema;

// Builds the declaration of one position from its draft, for SchemaBuilder.
//
// A declaration states what its type has and nothing else: text policies only for a String,
// an enum type only for an Enum, an owned type only for an Owned value, an entity type only
// for a Reference, an element only for a List and a Set, a key and a value only for a Map. A
// policy stated for another type would mean nothing, so it is a mistake in the descriptor
// rather than something to ignore. The types a declaration names are resolved here, and the
// positions inside a collection are built in the same way. A message names the position: a
// field, or the element, the key or the value inside the declaration of one.
//
// What a declaration may name is added before it is built: the primary keys of the entity
// types first, because a reference holds one, then the owned types one by one in the order of
// their containment, so that an owned type is there before a declaration that holds it.
internal sealed class DeclarationBuilder
{
    private readonly Dictionary<string, LatticeEnumType> _enumTypes;
    private readonly Dictionary<string, ReferenceTarget> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LatticeOwnedType> _ownedTypes = new(StringComparer.Ordinal);

    public DeclarationBuilder(IReadOnlyList<LatticeEnumType> enumTypes)
    {
        _enumTypes = enumTypes.ToDictionary(enumType => enumType.Name, StringComparer.Ordinal);
    }

    public void AddTarget(ReferenceTarget target) => _targets.Add(target.EntityTypeName, target);

    public void AddOwnedType(LatticeOwnedType ownedType) => _ownedTypes.Add(ownedType.Name, ownedType);

    // The entity type of the given name as a reference refers to it, with its primary key.
    public ReferenceTarget TargetOf(string entityTypeName) => _targets[entityTypeName];

    public LatticeValueDeclaration Build(ValueDeclarationDraft draft, string place)
    {
        if (!Enum.IsDefined(draft.Type))
        {
            throw SchemaBuilder.Invalid($"{place} has an unknown type.");
        }

        CheckOnlyWhatTheTypeHas(draft, place);

        return draft.Type switch
        {
            LatticeFieldTypeEnum.String => BuildString(draft, place),
            LatticeFieldTypeEnum.Enum => new LatticeValueDeclaration(draft.Type, draft.IsNullable, enumType: ResolveEnumType(draft, place)),
            LatticeFieldTypeEnum.Owned => new LatticeValueDeclaration(draft.Type, draft.IsNullable, ownedType: ResolveOwnedType(draft, place)),
            LatticeFieldTypeEnum.Reference => new LatticeValueDeclaration(draft.Type, draft.IsNullable, target: ResolveTarget(draft, place)),
            LatticeFieldTypeEnum.List => BuildList(draft, place),
            LatticeFieldTypeEnum.Set => BuildSet(draft, place),
            LatticeFieldTypeEnum.Map => BuildMap(draft, place),
            _ => new LatticeValueDeclaration(draft.Type, draft.IsNullable),
        };
    }

    private static void CheckOnlyWhatTheTypeHas(ValueDeclarationDraft draft, string place)
    {
        var type = draft.Type;
        if (type != LatticeFieldTypeEnum.String && (draft.Comparison is not null || draft.StringStorage is not null))
        {
            throw SchemaBuilder.Invalid($"{place} is {type}; a Comparison and a StringStorage apply only to a String.");
        }

        if (type != LatticeFieldTypeEnum.Enum && draft.EnumTypeName is { } enumTypeName)
        {
            throw SchemaBuilder.Invalid($"{place} is {type} and names the enum type '{SchemaBuilder.Shown(enumTypeName)}'; only a declaration of type Enum names one.");
        }

        if (type != LatticeFieldTypeEnum.Owned && draft.OwnedTypeName is { } ownedTypeName)
        {
            throw SchemaBuilder.Invalid($"{place} is {type} and names the owned type '{SchemaBuilder.Shown(ownedTypeName)}'; only a declaration of type Owned names one.");
        }

        if (type != LatticeFieldTypeEnum.Reference && draft.EntityTypeName is { } entityTypeName)
        {
            throw SchemaBuilder.Invalid($"{place} is {type} and names the entity type '{SchemaBuilder.Shown(entityTypeName)}'; only a declaration of type Reference names one.");
        }

        if (type is not (LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set) && draft.Element is not null)
        {
            throw SchemaBuilder.Invalid($"{place} is {type} and declares an Element; only a List and a Set have one.");
        }

        if (type != LatticeFieldTypeEnum.Map && (draft.Key is not null || draft.Value is not null))
        {
            throw SchemaBuilder.Invalid($"{place} is {type} and declares a {(draft.Key is not null ? "Key" : "Value")}; only a Map has one.");
        }
    }

    private static LatticeValueDeclaration BuildString(ValueDeclarationDraft draft, string place)
    {
        if (draft.Comparison is { } comparison && !Enum.IsDefined(comparison))
        {
            throw SchemaBuilder.Invalid($"{place} has an unknown comparison.");
        }

        if (draft.StringStorage is { } stringStorage && !Enum.IsDefined(stringStorage))
        {
            throw SchemaBuilder.Invalid($"{place} has an unknown string storage.");
        }

        return new LatticeValueDeclaration(
            draft.Type,
            draft.IsNullable,
            draft.Comparison ?? LatticeStringComparisonEnum.Ordinal,
            draft.StringStorage ?? LatticeStringStorageEnum.Plain);
    }

    private LatticeEnumType ResolveEnumType(ValueDeclarationDraft draft, string place)
    {
        if (draft.EnumTypeName is not { } name)
        {
            throw SchemaBuilder.Invalid($"{place} is of type Enum and names no enum type.");
        }

        return _enumTypes.TryGetValue(name, out var enumType)
            ? enumType
            : throw SchemaBuilder.Invalid($"{place} names the enum type '{SchemaBuilder.Shown(name)}', which the schema does not declare.");
    }

    private LatticeOwnedType ResolveOwnedType(ValueDeclarationDraft draft, string place)
    {
        if (draft.OwnedTypeName is not { } name)
        {
            throw SchemaBuilder.Invalid($"{place} is of type Owned and names no owned type.");
        }

        return _ownedTypes.TryGetValue(name, out var ownedType)
            ? ownedType
            : throw SchemaBuilder.Invalid($"{place} names the owned type '{SchemaBuilder.Shown(name)}', which the schema does not declare.");
    }

    private ReferenceTarget ResolveTarget(ValueDeclarationDraft draft, string place)
    {
        if (draft.EntityTypeName is not { } name)
        {
            throw SchemaBuilder.Invalid($"{place} is of type Reference and names no entity type.");
        }

        return _targets.TryGetValue(name, out var target)
            ? target
            : throw SchemaBuilder.Invalid($"{place} names the entity type '{SchemaBuilder.Shown(name)}', which the schema does not declare.");
    }

    private LatticeValueDeclaration BuildList(ValueDeclarationDraft draft, string place)
    {
        var element = Build(Required(draft.Element, draft, "Element", place), $"the element of {place}");

        return new LatticeValueDeclaration(draft.Type, draft.IsNullable, element: element);
    }

    private LatticeValueDeclaration BuildSet(ValueDeclarationDraft draft, string place)
    {
        var elementPlace = $"the element of {place}";
        var element = Build(Required(draft.Element, draft, "Element", place), elementPlace);
        CheckEquality(element, elementPlace, "a Set element");

        return new LatticeValueDeclaration(draft.Type, draft.IsNullable, element: element);
    }

    private LatticeValueDeclaration BuildMap(ValueDeclarationDraft draft, string place)
    {
        var keyDraft = Required(draft.Key, draft, "Key", place);
        var valueDraft = Required(draft.Value, draft, "Value", place);

        var keyPlace = $"the key of {place}";
        var key = Build(keyDraft, keyPlace);
        if (key.IsNullable)
        {
            throw SchemaBuilder.Invalid($"{keyPlace} is nullable; a Map key never is.");
        }

        CheckEquality(key, keyPlace, "a Map key");
        var value = Build(valueDraft, $"the value of {place}");

        return new LatticeValueDeclaration(draft.Type, draft.IsNullable, key: key, value: value);
    }

    // A Map key and a Set element are told apart by their equality, which only some types
    // have.
    private static void CheckEquality(LatticeValueDeclaration declaration, string place, string position)
    {
        if (!FieldTypes.HasEquality(declaration.Type))
        {
            throw SchemaBuilder.Invalid($"{place} is {declaration.Type}; {position} is a scalar that is not Binary, an Enum or a Reference.");
        }
    }

    private static ValueDeclarationDraft Required(ValueDeclarationDraft? inner, ValueDeclarationDraft draft, string property, string place) =>
        inner ?? throw SchemaBuilder.Invalid($"{place} is a {draft.Type} and declares no {property}.");
}
