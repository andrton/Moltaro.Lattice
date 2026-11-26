using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Updates;

// Takes private operands before the caller can change them. Shape errors are
// reported here. Whole collections keep every entry until the validation of
// the snapshot, where they acquire the declared comparison and representation.
internal static class UpdateValues
{
    public static object? Capture<T>(LatticeValueDeclaration declaration, T value, UpdateValueBinding? binding, string field)
    {
        var scalar = !FieldTypes.IsNested(declaration.Type) && declaration.Type != LatticeFieldTypeEnum.Binary;
        if (value is null)
        {
            if (scalar && !IsBoundEnum(typeof(T), binding))
            {
                IndexValue.Of(value).BindShape(declaration, field, nameof(value));
            }
            else if (!scalar && typeof(T) != typeof(object) && !AcceptsType(declaration, binding, typeof(T)))
            {
                throw WrongType(declaration, field, typeof(T));
            }

            return CheckNull(declaration, field);
        }

        if (scalar && !IsBoundEnum(value.GetType(), binding))
        {
            return IndexValue.Of(value).BindShape(declaration, field, nameof(value)).ToObject();
        }

        return CaptureExact(declaration, value, binding, field);
    }

    private static bool IsBoundEnum(Type type, UpdateValueBinding? binding) =>
        binding is { EnumToInteger: not null } && (type == binding.ValueType || type == binding.NullableEnumType);

    private static bool AcceptsType(LatticeValueDeclaration declaration, UpdateValueBinding? binding, Type type) => declaration.Type switch
    {
        LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set => (binding?.Sequence ?? declaration.Sequence!).IsInputType(type),
        LatticeFieldTypeEnum.Map => (binding?.Map ?? declaration.Map!).IsInputType(type),
        LatticeFieldTypeEnum.Owned => (binding?.ValueType ?? declaration.PositionType).IsAssignableFrom(type),
        _ => type == declaration.PositionType,
    };

    private static object CaptureExact(LatticeValueDeclaration declaration, object value, UpdateValueBinding? binding, string field)
    {
        switch (declaration.Type)
        {
            case LatticeFieldTypeEnum.Owned:
                return CaptureOwned(declaration.OwnedType!, value, binding, field);

            case LatticeFieldTypeEnum.List:
            case LatticeFieldTypeEnum.Set:
                var sequence = binding?.Sequence ?? declaration.Sequence!;
                if (!sequence.IsInput(value))
                {
                    throw WrongType(declaration, field, value.GetType());
                }

                var elements = new List<object?>();
                foreach (var member in sequence.Elements(value))
                {
                    elements.Add(CaptureEntry(declaration.Element!, member, binding?.Element, field));
                }

                return elements;

            case LatticeFieldTypeEnum.Map:
                var map = binding?.Map ?? declaration.Map!;
                if (!map.IsInput(value))
                {
                    throw WrongType(declaration, field, value.GetType());
                }

                var entries = new List<KeyValuePair<object, object?>>();
                foreach (var (key, item) in map.Entries(value))
                {
                    var copiedKey = CaptureEntry(declaration.Key!, key, binding?.Key, field);
                    var copiedItem = CaptureEntry(declaration.Value!, item, binding?.Value, field);
                    entries.Add(new(copiedKey!, copiedItem));
                }

                return entries;

            case LatticeFieldTypeEnum.Enum when IsBoundEnum(value.GetType(), binding):
                return binding!.EnumToInteger!(value);
        }

        if (value.GetType() != declaration.ValueType)
        {
            throw WrongType(declaration, field, value.GetType());
        }

        return declaration.Type == LatticeFieldTypeEnum.Binary ? ((byte[])value).Clone() : value;
    }

    private static LatticeOwnedValue CaptureOwned(LatticeOwnedType type, object value, UpdateValueBinding? binding, string field)
    {
        var owned = new LatticeOwnedValue(type);
        if (binding is { OwnedFields: { } properties, ValueType: { } cls } && cls.IsInstanceOfType(value))
        {
            foreach (var property in properties)
            {
                if (property.Get(value) is { } child)
                {
                    owned.Values[property.Field.Ordinal] = CaptureExact(property.Field.Declaration, child, property.Update, property.Field.Name);
                }
            }

            return owned;
        }

        if (binding is not null || value is not LatticeOwnedValue record || !ReferenceEquals(record.OwnedType, type))
        {
            throw new ArgumentException($"Field '{field}' holds an owned value of '{type.Name}'.", nameof(value));
        }

        foreach (var member in type.FieldsByOrdinal)
        {
            if (record.Values[member.Ordinal] is { } child)
            {
                owned.Values[member.Ordinal] = CaptureExact(member.Declaration, child, null, member.Name);
            }
        }

        return owned;
    }

    private static object? CaptureEntry(LatticeValueDeclaration declaration, object? value, UpdateValueBinding? binding, string field) =>
        value is null ? CheckNull(declaration, field) : CaptureExact(declaration, value, binding, field);

    private static object? CheckNull(LatticeValueDeclaration declaration, string field) =>
        declaration.IsNullable ? null : throw new ArgumentException($"Field '{field}' does not allow null at this position.", "value");

    private static ArgumentException WrongType(LatticeValueDeclaration declaration, string field, Type type) =>
        new($"Field '{field}' is {declaration.Type}; a {type.Name} cannot be used at this position.", "value");

    public static object? Validate(LatticeValueDeclaration declaration, object? snapshot, LatticeEntityType entityType, LatticeField field,
        MemoryLease? lease = null)
    {
        if (snapshot is null)
        {
            return declaration.IsNullable
                ? null
                : throw Refuse(entityType, field, "is required and has no value.");
        }

        switch (declaration.Type)
        {
            case LatticeFieldTypeEnum.Owned:
                var source = (LatticeOwnedValue)snapshot;
                var owned = LatticeOwnedValue.Snapshot(source.OwnedType, lease);
                foreach (var member in source.OwnedType.FieldsByOrdinal)
                {
                    owned.Values[member.Ordinal] = Validate(member.Declaration, source.Values[member.Ordinal], entityType, member, lease);
                }

                return owned;

            case LatticeFieldTypeEnum.List:
            case LatticeFieldTypeEnum.Set:
                var elements = (List<object?>)snapshot;
                var sequence = declaration.Sequence!;
                var collection = sequence.Create(elements.Count, lease);
                foreach (var element in elements)
                {
                    sequence.Add(collection, Validate(declaration.Element!, element, entityType, field, lease), lease);
                }

                return collection;

            case LatticeFieldTypeEnum.Map:
                var entries = (List<KeyValuePair<object, object?>>)snapshot;
                var map = declaration.Map!;
                var result = map.Create(entries.Count, lease);
                foreach (var (key, value) in entries)
                {
                    var validatedKey = Validate(declaration.Key!, key, entityType, field, lease)!;
                    var validatedValue = Validate(declaration.Value!, value, entityType, field, lease);
                    if (!map.TryAdd(result, validatedKey, validatedValue, lease))
                    {
                        throw Refuse(entityType, field, $"holds the Map key '{validatedKey}' more than once under the schema comparison.");
                    }
                }

                return result;
        }

        RecordValues.CheckCopiedLeaf(declaration, snapshot, entityType, field);
        if (declaration.Type == LatticeFieldTypeEnum.Binary)
        {
            // The command owns this copy until Consume. A successful Add transfers the
            // same array while a refused Add leaves it available for another attempt.
            lease?.AdoptArray((byte[])snapshot, nameof(LatticeUpdate));
        }

        return snapshot;
    }

    private static LatticeException Refuse(LatticeEntityType entityType, LatticeField field, string message) =>
        new(LatticeErrorEnum.ValidationFailed, $"{entityType.Name}.{field.Name} {message}");
}
