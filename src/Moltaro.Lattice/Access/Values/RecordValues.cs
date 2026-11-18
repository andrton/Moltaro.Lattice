using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Values;

// What a record and an owned value hold of what they are given, and what a write takes of a
// record: every value is copied at every depth into the form a record holds, see
// LatticeValueDeclaration.ValueType, and checked against its declaration on the way. Nothing
// of the caller's is kept: a collection is rebuilt as the record's own List, Dictionary or
// HashSet with the comparers of the schema, an owned value is copied field by field, a byte
// array is copied.
//
// The same walk serves two moments, which differ in what they report:
//   - a value set into a field of a record or of an owned value, which may be any input the
//     declaration accepts; a mismatch is an ArgumentException that names the field, and for a
//     value inside an owned value the field of the owned value. A field without a value is
//     allowed here; the write finds it.
//   - a record submitted by a write, which holds only what it took, changed perhaps through
//     what it returned; a mismatch, a field without the value it requires at any depth, text
//     that is not well-formed at any depth, an integer that its enum type does not allow at any
//     depth, and the value of a field above the bound of one value, are ValidationFailed.
//
// The walk descends by the declarations of the schema, never by the objects it meets: at each
// step it goes from a declaration into one inside it, or into the fields of an owned type the
// declaration names, and it checks that a value is of the declared type before it looks into
// it. An owned type does not contain itself, so this ends, and it goes no deeper than the
// declaration, which is at most as deep as SchemaBuilder allows, whatever the caller built: a
// collection added to itself through what a record returned cannot be added, because no
// collection is of its own element type, and an owned value of another owned type is refused
// before it is entered.
internal static class RecordValues
{
    // A value that is not null, set into a field of a record or of an owned value. A value
    // given by itself, a scalar, an Enum or a Reference, accepts an integer of another width
    // when it fits; the types inside a collection are exact.
    public static object Accept(LatticeField field, string owner, object value)
    {
        var declaration = field.Declaration;
        if (FieldTypes.IsNested(declaration.Type) || declaration.Type == LatticeFieldTypeEnum.Binary)
        {
            return Copy(declaration, value, new Place(EntityType: null, owner, field));
        }

        return FieldValues.Normalize(field, value, nameof(value));
    }

    // The values of a record as a write takes them: copied and checked again at every depth. The
    // value of every field of the entity is checked against the bound of one value first, so that a
    // value above it is refused before it is copied. A String or Binary value inside a collection or
    // an owned value is not: whether the bound holds for it is the planner's, with the contracts of
    // S18 and S19 (Q35, point 7, of Epic 1), and no entity type with such a field is stored yet.
    //
    // A Binary value of a field of the entity is copied when copyBinary is true, for a write batch,
    // which copies an operation when it is added. When it is false the array the record holds is taken as it is, after the same check that
    // it is a byte[] and nothing else: the engine copies it once, into its own memory, while the write
    // runs, and the caller keeps it unchanged until the write completes (the planner's answer to Q43,
    // from the accepted decision "Stable write inputs until completion"). A Binary value inside a
    // collection or an owned value is copied with it either way; the engine stores no such field yet.
    public static object?[] Submit(LatticeEntityType entityType, object?[] values, bool copyBinary)
    {
        var fields = entityType.Fields;

        // By index: a foreach over the list of the schema would allocate its enumerator.
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];

            if (values[field.Ordinal] is { } value && !ValueBound.Holds(field, value, entityType.StringEncoding))
            {
                throw new Place(entityType.Name, entityType.Name, field).Fail(ValueBound.Problem);
            }
        }

        var submitted = new object?[values.Length];
        CopyFields(entityType.Fields, values, submitted, entityType.Name, entityType.Name, copyBinary);

        return submitted;
    }

    private static object Copy(LatticeValueDeclaration declaration, object value, Place place) => declaration.Type switch
    {
        LatticeFieldTypeEnum.Owned => CopyOwned(declaration.OwnedType!, value, place),
        LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set => CopySequence(declaration, value, place),
        LatticeFieldTypeEnum.Map => CopyMap(declaration, value, place),
        LatticeFieldTypeEnum.Binary => TakeBinary(declaration, value, place, copy: true),
        LatticeFieldTypeEnum.String => CopyText(declaration, value, place),
        LatticeFieldTypeEnum.Enum => CopyEnum(declaration, value, place),
        _ => value.GetType() == declaration.ValueType ? value : throw place.Fail(NotAccepted(declaration, value)),
    };

    // Exactly a byte[]: the runtime also takes an sbyte[] for one, whose values are others. A copy of
    // it, or the array itself when copy is false (Submit).
    private static object TakeBinary(LatticeValueDeclaration declaration, object value, Place place, bool copy)
    {
        if (value.GetType() != typeof(byte[]))
        {
            throw place.Fail(NotAccepted(declaration, value));
        }

        return copy ? ((byte[])value).Clone() : value;
    }

    // An owned value is copied field by field, as if each field were set again, so what it
    // holds is checked as a field of its owned type, and a message names that field.
    private static LatticeOwnedValue CopyOwned(LatticeOwnedType ownedType, object value, Place place)
    {
        if (value is not LatticeOwnedValue source || !ReferenceEquals(source.OwnedType, ownedType))
        {
            var given = value is LatticeOwnedValue other ? $"an owned value of '{other.OwnedType.Name}'" : $"a {value.GetType().Name}";
            throw place.Fail($"holds owned values of '{ownedType.Name}'; {given} cannot be used for it.");
        }

        var copy = new LatticeOwnedValue(ownedType);
        CopyFields(ownedType.Fields, source.Values, copy.Values, place.EntityType, ownedType.Name, copyBinary: true);

        return copy;
    }

    // The fields of an entity type or of an owned type, the owner. A field without a value is
    // refused only by a write, which names the entity type it writes. A Binary value of one of these
    // fields is copied when copyBinary is true (Submit).
    private static void CopyFields(IReadOnlyList<LatticeField> fields, object?[] values, object?[] into, string? entityType, string owner, bool copyBinary)
    {
        foreach (var field in fields)
        {
            var place = new Place(entityType, owner, field);
            var value = values[field.Ordinal];
            if (value is not null)
            {
                into[field.Ordinal] = field.Declaration.Type == LatticeFieldTypeEnum.Binary
                    ? TakeBinary(field.Declaration, value, place, copyBinary)
                    : Copy(field.Declaration, value, place);
            }
            else if (entityType is not null && !field.Declaration.IsNullable)
            {
                throw place.Fail("is required and has no value.");
            }
        }
    }

    private static object CopySequence(LatticeValueDeclaration declaration, object value, Place place)
    {
        var form = declaration.Sequence!;
        if (!form.IsInput(value))
        {
            throw place.Fail(NotAccepted(declaration, value));
        }

        var element = declaration.Element!;
        var copy = form.Create();
        foreach (var item in form.Elements(value))
        {
            form.Add(copy, item is null ? Null(element, place) : Copy(element, item, place));
        }

        return copy;
    }

    // A key given twice in the input is refused, also with the same value: the input does not
    // say which entry is meant. Two keys are the same key when the key declaration holds them
    // equal, whatever the comparer of the input.
    private static object CopyMap(LatticeValueDeclaration declaration, object value, Place place)
    {
        var form = declaration.Map!;
        if (!form.IsInput(value))
        {
            throw place.Fail(NotAccepted(declaration, value));
        }

        var copy = form.Create();
        foreach (var (key, item) in form.Entries(value))
        {
            if (key is null)
            {
                throw place.Fail("has a null key; a Map key is never null.");
            }

            var copiedKey = Copy(declaration.Key!, key, place);
            var copiedItem = item is null ? Null(declaration.Value!, place) : Copy(declaration.Value!, item, place);
            if (!form.TryAdd(copy, copiedKey, copiedItem))
            {
                throw place.Fail($"has the key '{key}' twice, as its key declaration compares keys.");
            }
        }

        return copy;
    }

    // Text is held as it is given. A write refuses text that is not well-formed, a surrogate without
    // its pair, at any depth (the planner's answer to Q41); a value set into a record is not checked
    // for it.
    private static object CopyText(LatticeValueDeclaration declaration, object value, Place place)
    {
        if (value is not string text)
        {
            throw place.Fail(NotAccepted(declaration, value));
        }

        if (place.IsWrite && !WellFormedText.IsWellFormed(text))
        {
            throw place.Fail("holds text that is not well-formed: it has a surrogate without its pair.");
        }

        return text;
    }

    // An Enum value is held as the integer of the underlying type of its enum type. A write refuses an
    // integer that the enum type does not allow, at any depth (EnumValues); a value set into a record is
    // not checked for it.
    private static object CopyEnum(LatticeValueDeclaration declaration, object value, Place place)
    {
        if (value.GetType() != declaration.ValueType)
        {
            throw place.Fail(NotAccepted(declaration, value));
        }

        if (place.IsWrite && !EnumValues.IsValid(declaration.EnumType!, value))
        {
            throw place.Fail($"holds {EnumValues.Undeclared(declaration.EnumType!, value)}.");
        }

        return value;
    }

    private static object? Null(LatticeValueDeclaration inner, Place place) =>
        inner.IsNullable ? null : throw place.Fail("holds a null where its declaration does not allow one.");

    private static string NotAccepted(LatticeValueDeclaration declaration, object value) =>
        $"is {Describe(declaration)}; a {value.GetType().Name} cannot be used for it.";

    // A declaration in words, at every depth: List of Int64, Map of String to Owned 'Address'.
    private static string Describe(LatticeValueDeclaration declaration) => declaration.Type switch
    {
        LatticeFieldTypeEnum.Owned => $"Owned '{declaration.OwnedType!.Name}'",
        LatticeFieldTypeEnum.Reference => $"a Reference to '{declaration.ReferencedEntityTypeName}', held as {declaration.ValueType.Name}",
        LatticeFieldTypeEnum.Enum => $"Enum '{declaration.EnumType!.Name}', held as {declaration.ValueType.Name}",
        LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set => $"a {declaration.Type} of {Inner(declaration.Element!)}",
        LatticeFieldTypeEnum.Map => $"a Map of {Inner(declaration.Key!)} to {Inner(declaration.Value!)}",
        _ => declaration.Type.ToString(),
    };

    private static string Inner(LatticeValueDeclaration declaration) =>
        declaration.IsNullable ? $"nullable {Describe(declaration)}" : Describe(declaration);

    // Where a value is: the field of an entity type or of an owned type that holds it, and,
    // for a write, the entity type that is written. The message is made only on a failure.
    private readonly record struct Place(string? EntityType, string Owner, LatticeField Field)
    {
        // Whether the value is submitted by a write, which names the entity type it writes, rather
        // than set into a record.
        public bool IsWrite => EntityType is not null;

        public Exception Fail(string problem) => EntityType is null
            ? new ArgumentException($"Field '{Owner}.{Field.Name}' {problem}", "value")
            : new LatticeException(LatticeErrorEnum.ValidationFailed, $"{EntityType}: field '{Owner}.{Field.Name}' {problem}");
    }
}
