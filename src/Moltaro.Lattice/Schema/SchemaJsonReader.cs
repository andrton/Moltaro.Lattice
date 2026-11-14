using System.Globalization;
using System.Text.Json;

namespace Moltaro.Lattice.Schema;

// Reads the JSON schema descriptor into drafts, which SchemaBuilder validates.
//
// The descriptor is closed: a property its form does not have is refused, so that a misspelt
// policy is not read as its default. The reader checks the form only: which properties a
// declaration has, which of them are required, and that a value has the JSON kind its
// property takes and fits the type it is read as. Every rule of the model is SchemaBuilder's.
internal static class SchemaJsonReader
{
    private const string Descriptor = "the descriptor";

    // The properties of a declaration: of a field besides its identity and its name, and of
    // the element, the key and the value of a collection. Never changed, so it is shared.
    private static readonly string[] DeclarationProperties =
        ["Type", "Nullable", "Comparison", "StringStorage", "Enum", "OwnedType", "EntityType", "Element", "Key", "Value"];

    public static LatticeSchema Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw SchemaBuilder.Invalid("the JSON descriptor cannot be read.", exception);
        }
        catch (ArgumentException exception)
        {
            // The text has a lone surrogate, which no encoding can hold.
            throw SchemaBuilder.Invalid("the JSON descriptor is not well-formed text.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            CheckText(root);
            CheckProperties(root, Descriptor, "SchemaVersion", "StringEncoding", "Enums", "OwnedTypes", "EntityTypes");

            var version = root.TryGetProperty("SchemaVersion", out var versionElement)
                ? Int32(versionElement, "SchemaVersion", Descriptor)
                : 1;

            var stringEncoding = root.TryGetProperty("StringEncoding", out var encodingElement)
                ? Name<LatticeStringEncodingEnum>(encodingElement, "StringEncoding", Descriptor)
                : LatticeStringEncodingEnum.Utf8;

            List<EnumTypeDraft> enumTypes = root.TryGetProperty("Enums", out var enumElements)
                ? ReadList(enumElements, "Enums", Descriptor, ReadEnumType)
                : [];

            List<OwnedTypeDraft> ownedTypes = root.TryGetProperty("OwnedTypes", out var ownedElements)
                ? ReadList(ownedElements, "OwnedTypes", Descriptor, ReadOwnedType)
                : [];

            var entityTypes = ReadList(Required(root, "EntityTypes", Descriptor), "EntityTypes", Descriptor, ReadEntityType);

            return SchemaBuilder.BuildSchema(version, stringEncoding, enumTypes, ownedTypes, entityTypes);
        }
    }

    private static EnumTypeDraft ReadEnumType(JsonElement element, int position)
    {
        var place = Place(element, "enum type", position, owner: null);
        CheckProperties(element, place, "Id", "Name", "UnderlyingType", "Flags", "Members");

        var id = Identity(element, place);
        var name = OptionalString(element, "Name", place);

        var underlyingType = element.TryGetProperty("UnderlyingType", out var underlyingTypeElement)
            ? Name<LatticeFieldTypeEnum>(underlyingTypeElement, "UnderlyingType", place)
            : LatticeFieldTypeEnum.Int32;

        var isFlags = OptionalBoolean(element, "Flags", place);
        var members = ReadList(Required(element, "Members", place), "Members", place, (member, index) => ReadEnumMember(member, index, name));

        return new EnumTypeDraft(id, name, underlyingType, isFlags, members);
    }

    private static EnumMemberDraft ReadEnumMember(JsonElement element, int position, string? owner)
    {
        var place = Place(element, "member", position, owner);
        CheckProperties(element, place, "Name", "Value", "Canonical");

        var name = OptionalString(element, "Name", place);
        var value = Integer(Required(element, "Value", place), "Value", place);
        var isCanonical = OptionalBoolean(element, "Canonical", place);

        return new EnumMemberDraft(name, value, isCanonical);
    }

    private static OwnedTypeDraft ReadOwnedType(JsonElement element, int position)
    {
        var place = Place(element, "owned type", position, owner: null);
        CheckProperties(element, place, "Id", "Name", "ClrType", "Fields");

        var id = Identity(element, place);
        var name = OptionalString(element, "Name", place);
        var clrTypeName = OptionalString(element, "ClrType", place);
        var fields = ReadList(Required(element, "Fields", place), "Fields", place, (field, index) => ReadField(field, index, name));

        return new OwnedTypeDraft(id, name, clrTypeName, fields);
    }

    private static EntityTypeDraft ReadEntityType(JsonElement element, int position)
    {
        var place = Place(element, "entity type", position, owner: null);
        CheckProperties(element, place, "Id", "Name", "ClrType", "Fields", "PrimaryKey", "Indexes");

        var id = Identity(element, place);
        var name = OptionalString(element, "Name", place);
        var clrTypeName = OptionalString(element, "ClrType", place);
        var fields = ReadList(Required(element, "Fields", place), "Fields", place, (field, index) => ReadField(field, index, name));

        var primaryKey = ReadList(Required(element, "PrimaryKey", place), "PrimaryKey", place, (key, _) => Int32(key, "PrimaryKey", place));
        if (primaryKey.Count != 1)
        {
            throw SchemaBuilder.Invalid($"{place} must have exactly one primary key field.");
        }

        List<IndexDraft> indexes = element.TryGetProperty("Indexes", out var indexElements)
            ? ReadList(indexElements, "Indexes", place, (index, indexPosition) => ReadIndex(index, indexPosition, name))
            : [];

        return new EntityTypeDraft(id, name, clrTypeName, fields, primaryKey[0], indexes);
    }

    private static FieldDraft ReadField(JsonElement element, int position, string? owner)
    {
        var place = Place(element, "field", position, owner);
        CheckProperties(element, place, ["Id", "Name", .. DeclarationProperties]);

        var id = Identity(element, place);
        var name = OptionalString(element, "Name", place);

        return new FieldDraft(id, name, ReadDeclaration(element, place));
    }

    // What a field holds: everything a field declares except its identity and its name. The
    // element, the key and the value of a collection are declarations of their own, with
    // these properties and no identity and no name. Their nesting is bounded by the depth that
    // JsonDocument reads.
    private static ValueDeclarationDraft ReadDeclaration(JsonElement element, string place)
    {
        var type = Name<LatticeFieldTypeEnum>(Required(element, "Type", place), "Type", place);
        var isNullable = OptionalBoolean(element, "Nullable", place);

        LatticeStringComparisonEnum? comparison = element.TryGetProperty("Comparison", out var comparisonElement)
            ? Name<LatticeStringComparisonEnum>(comparisonElement, "Comparison", place)
            : null;

        LatticeStringStorageEnum? stringStorage = element.TryGetProperty("StringStorage", out var storageElement)
            ? Name<LatticeStringStorageEnum>(storageElement, "StringStorage", place)
            : null;

        var enumTypeName = OptionalString(element, "Enum", place);
        var ownedTypeName = OptionalString(element, "OwnedType", place);
        var entityTypeName = OptionalString(element, "EntityType", place);

        return new ValueDeclarationDraft(
            type,
            isNullable,
            comparison,
            stringStorage,
            enumTypeName,
            ownedTypeName,
            entityTypeName,
            ReadInnerDeclaration(element, "Element", $"the element of {place}"),
            ReadInnerDeclaration(element, "Key", $"the key of {place}"),
            ReadInnerDeclaration(element, "Value", $"the value of {place}"));
    }

    private static ValueDeclarationDraft? ReadInnerDeclaration(JsonElement element, string property, string place)
    {
        if (!element.TryGetProperty(property, out var inner))
        {
            return null;
        }

        CheckProperties(inner, place, DeclarationProperties);
        return ReadDeclaration(inner, place);
    }

    private static IndexDraft ReadIndex(JsonElement element, int position, string? owner)
    {
        var place = Place(element, "index", position, owner);
        CheckProperties(element, place, "Id", "Name", "Fields", "Unique");

        var id = Identity(element, place);
        var name = OptionalString(element, "Name", place);
        var parts = ReadList(Required(element, "Fields", place), "Fields", place, (part, _) => ReadPart(part, place));
        var isUnique = OptionalBoolean(element, "Unique", place);

        return new IndexDraft(id, name, parts, isUnique);
    }

    // A part is one field identity, or a list of identities from a field of the entity type
    // down to the indexed field. One identity is the path of that field alone.
    private static IReadOnlyList<int> ReadPart(JsonElement part, string place) =>
        part.ValueKind == JsonValueKind.Array
            ? ReadList(part, "Fields", place, (step, _) => Int32(step, "Fields", place))
            : [Int32(part, "Fields", place)];

    // Names a declaration in a message by its name, with the name of what it belongs to, or
    // by its position when it has no name.
    private static string Place(JsonElement element, string kind, int position, string? owner)
    {
        var name = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        if (string.IsNullOrEmpty(name))
        {
            return owner is null ? $"{kind} number {position + 1}" : $"{kind} number {position + 1} of '{SchemaBuilder.Shown(owner)}'";
        }

        return owner is null ? $"{kind} '{SchemaBuilder.Shown(name)}'" : $"{kind} '{SchemaBuilder.Shown(owner)}.{SchemaBuilder.Shown(name)}'";
    }

    // JsonDocument accepts an escaped lone surrogate, such as \ud800, and fails only when the
    // text is taken as a .NET string. Every property name and string is taken here once, so
    // that the rest of the reader works on well-formed text.
    private static void CheckText(JsonElement root)
    {
        try
        {
            TakeText(root);
        }
        catch (InvalidOperationException exception)
        {
            throw SchemaBuilder.Invalid("the JSON descriptor has text that is not well-formed: an escaped surrogate without its pair.", exception);
        }

        static void TakeText(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        _ = property.Name;
                        TakeText(property.Value);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        TakeText(item);
                    }

                    break;

                case JsonValueKind.String:
                    _ = element.GetString();
                    break;
            }
        }
    }

    // The declaration is an object with properties of its form only, each at most once: of a
    // property given twice, one value would be taken without a word about the other.
    private static void CheckProperties(JsonElement element, string place, params ReadOnlySpan<string> properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw SchemaBuilder.Invalid($"{place} is not a JSON object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.Contains(property.Name))
            {
                throw SchemaBuilder.Invalid($"{place} has the unknown property '{SchemaBuilder.Shown(property.Name)}'.");
            }

            if (!seen.Add(property.Name))
            {
                throw SchemaBuilder.Invalid($"{place} has the property '{SchemaBuilder.Shown(property.Name)}' twice.");
            }
        }
    }

    private static JsonElement Required(JsonElement element, string property, string place) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw SchemaBuilder.Invalid($"{place} has no {property}.");

    // A missing identity is refused here; a zero, a negative or a repeated one by SchemaBuilder.
    private static int Identity(JsonElement element, string place) =>
        Int32(Required(element, "Id", place), "Id", place);

    private static int Int32(JsonElement value, string property, string place) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw SchemaBuilder.Invalid($"the {property} of {place} is not an integer that an int can hold.");

    // The value of an enum member, read exactly from its text, so that every value of the
    // eight integer types is held without rounding and a value outside their range is seen as
    // such.
    private static Int128 Integer(JsonElement value, string property, string place)
    {
        var text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        if (text is not null && Int128.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        // A JSON number of digits alone, with no fraction and no exponent, is an integer: one
        // that is beyond Int128 is beyond every integer type an enum type can have.
        var isInteger = text is not null && text.TrimStart('-').All(char.IsAsciiDigit);
        throw SchemaBuilder.Invalid(isInteger
            ? $"{place} has the {property} {SchemaBuilder.Shown(text)}, outside the range of every integer type."
            : $"the {property} of {place} is not an integer.");
    }

    private static string? OptionalString(JsonElement element, string property, string place)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw SchemaBuilder.Invalid($"the {property} of {place} is not a string.");
    }

    private static bool OptionalBoolean(JsonElement element, string property, string place)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return false;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw SchemaBuilder.Invalid($"the {property} of {place} is not true or false.");
    }

    // One of the names of a public enum of the schema model, spelt exactly as the member.
    private static TEnum Name<TEnum>(JsonElement value, string property, string place)
        where TEnum : struct, Enum
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // TryParse also accepts numbers and lists of names; only a single name, spelt as the
        // member, is a name of the descriptor.
        if (text is null || !Enum.TryParse<TEnum>(text, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != text)
        {
            throw SchemaBuilder.Invalid($"{place} has the unknown {property} {SchemaBuilder.Shown(value.GetRawText())}.");
        }

        return parsed;
    }

    private static List<T> ReadList<T>(JsonElement value, string property, string place, Func<JsonElement, int, T> read)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw SchemaBuilder.Invalid($"the {property} of {place} is not an array.");
        }

        var items = new List<T>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            items.Add(read(item, items.Count));
        }

        return items;
    }
}
