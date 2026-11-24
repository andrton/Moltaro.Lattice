using System.Diagnostics;
using System.Linq.Expressions;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Sets;

// How a read sets one field of an object of a bound class: a setter compiled once, when the set is
// made, that takes the value in the .NET type of the field, so that a value is never boxed, and for a
// nullable field one that sets it to null. Both call the set accessor C# sees for the property
// (BoundProperty.SetAccessor), of any accessibility, virtually. No reflection runs when a value is
// set.
//
// A read sets the fields after it released the protection of the root, from its copy (ClassCopy): a
// value of a fixed-width type from the copy of the fixed part of the root, and a String or Binary
// value, which lies outside it, from the slot of the field among the values the read made under the
// protection. The set accessor is the application's code.
//
// An owned value or a List of the entity, a nested field, has a slot too, and is copied and set by
// members of its own (CopyNested, SetNested), in a pass of its own (ClassCopier): under the protection
// the read makes it in the form a record holds it, which only the types of the core and of .NET make
// (RootReader.GetOwned and GetList), and after the protection its setter turns that form into the value
// of the property (BoundProperty.FromRecord), which runs the constructors and the set accessors of the
// classes of the owned values it holds. The pass of its own is for a measured figure, the JIT listing of
// a Release build: with the nested fields as arms of CopyValue and Set, CopyValue grew from 328 to 370
// bytes and was no longer inlined into ClassCopier.Copy, which fell from 1,294 to 222 bytes and called
// it for each String or Binary field of every read, and Set no longer inlined DateOnly.FromDayNumber;
// with the pass, CopyValue is 328 bytes and inlined as before, and an entity type without nested fields
// pays a test of an empty array in Copy and in Build.
internal readonly struct ClassField<TEntity>
    where TEntity : class
{
    // The slot of a field that is no String, no Binary and no nested field.
    public const int NoSlot = -1;

    private readonly LatticeField _field;

    private readonly LatticeFieldTypeEnum _type;

    // An Action<TEntity, T> for the type T a record holds for the field (ValueType), which converts
    // to the type of the property, a Nullable<T> for a nullable value type.
    private readonly Delegate _setValue;

    private readonly Action<TEntity>? _setNull;

    private ClassField(LatticeField field, int slot, Delegate setValue, Action<TEntity>? setNull)
    {
        _field = field;

        // An Enum field is read as the integer of the underlying type of its enum type, and the setter
        // converts it to the .NET enum of the property.
        _type = field.Declaration.ScalarType;
        Slot = slot;
        _setValue = setValue;
        _setNull = setNull;
    }

    // Where a read keeps the value of a String or Binary field, or of a nested field, between the copy
    // and the object; NoSlot for any other field.
    public int Slot { get; }

    // The setters of a property of a field the engine stores: a scalar, an Enum, a Reference, a String, a
    // Binary or a nested field. A String or Binary field and a nested field get the given slot. The setter
    // of a nested field takes the form a record holds, as an object, and sets what FromRecord makes of it.
    public static ClassField<TEntity> Compile(BoundProperty property, int slot)
    {
        var declaration = property.Field.Declaration;
        var propertyType = property.Property.PropertyType;
        var entity = Expression.Parameter(typeof(TEntity), "entity");
        var nested = IsNested(property.Field);
        var value = Expression.Parameter(nested ? typeof(object) : declaration.ValueType, "value");
        Expression converted = value;

        if (nested)
        {
            converted = Expression.Convert(Expression.Invoke(Expression.Constant(property.FromRecord), value), propertyType);
        }
        else if (propertyType != declaration.ValueType)
        {
            converted = Expression.Convert(value, propertyType);
        }

        var setValue = Expression
            .Lambda(typeof(Action<,>).MakeGenericType(typeof(TEntity), value.Type), Expression.Call(entity, property.SetAccessor, converted), entity, value)
            .Compile();

        var setNull = declaration.IsNullable
            ? Expression.Lambda<Action<TEntity>>(Expression.Call(entity, property.SetAccessor, Expression.Default(propertyType)), entity).Compile()
            : null;

        return new ClassField<TEntity>(property.Field, slot, setValue, setNull);
    }

    // Whether the field is a String or a Binary, a Reference whose key is a String included, whose value
    // lies outside the fixed part of a root.
    public static bool HasSlot(LatticeField field) =>
        field.Declaration.ScalarType is LatticeFieldTypeEnum.String or LatticeFieldTypeEnum.Binary;

    // Whether the field is an owned value or a collection.
    public static bool IsNested(LatticeField field) =>
        field.Declaration.Type is LatticeFieldTypeEnum.Owned or LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set;

    // Under the protection of the root: makes the value of a String or Binary field into its slot,
    // the string or the array that the returned object will hold. Null for a null value.
    public void CopyValue(in RootReader root, object?[] values)
    {
        if (root.IsNull(_field))
        {
            values[Slot] = null;

            return;
        }

        values[Slot] = _type == LatticeFieldTypeEnum.String ? root.GetString(_field) : root.GetBinary(_field);
    }

    // After the protection: sets the property from the copy of the fixed part of the root, or, for a
    // String or Binary field, from its slot.
    public void Set(TEntity entity, in RootReader copied, object?[] values)
    {
        if (_setNull is not null && copied.IsNull(_field))
        {
            _setNull(entity);

            return;
        }

        switch (_type)
        {
            case LatticeFieldTypeEnum.Boolean:
                ((Action<TEntity, bool>)_setValue)(entity, copied.GetBoolean(_field));
                break;
            case LatticeFieldTypeEnum.Int8:
                ((Action<TEntity, sbyte>)_setValue)(entity, (sbyte)copied.GetInteger(_field));
                break;
            case LatticeFieldTypeEnum.Int16:
                ((Action<TEntity, short>)_setValue)(entity, (short)copied.GetInteger(_field));
                break;
            case LatticeFieldTypeEnum.Int32:
                ((Action<TEntity, int>)_setValue)(entity, (int)copied.GetInteger(_field));
                break;
            case LatticeFieldTypeEnum.Int64:
                ((Action<TEntity, long>)_setValue)(entity, copied.GetInteger(_field));
                break;
            case LatticeFieldTypeEnum.UInt8:
                ((Action<TEntity, byte>)_setValue)(entity, (byte)copied.GetUnsigned(_field));
                break;
            case LatticeFieldTypeEnum.UInt16:
                ((Action<TEntity, ushort>)_setValue)(entity, (ushort)copied.GetUnsigned(_field));
                break;
            case LatticeFieldTypeEnum.UInt32:
                ((Action<TEntity, uint>)_setValue)(entity, (uint)copied.GetUnsigned(_field));
                break;
            case LatticeFieldTypeEnum.UInt64:
                ((Action<TEntity, ulong>)_setValue)(entity, copied.GetUnsigned(_field));
                break;
            case LatticeFieldTypeEnum.Float32:
                ((Action<TEntity, float>)_setValue)(entity, copied.GetFloat32(_field));
                break;
            case LatticeFieldTypeEnum.Float64:
                ((Action<TEntity, double>)_setValue)(entity, copied.GetFloat64(_field));
                break;
            case LatticeFieldTypeEnum.Decimal:
                ((Action<TEntity, decimal>)_setValue)(entity, copied.GetDecimal(_field));
                break;
            case LatticeFieldTypeEnum.Guid:
                ((Action<TEntity, Guid>)_setValue)(entity, copied.GetGuid(_field));
                break;
            case LatticeFieldTypeEnum.DateTimeOffset:
                ((Action<TEntity, DateTimeOffset>)_setValue)(entity, copied.GetDateTimeOffset(_field));
                break;
            case LatticeFieldTypeEnum.DateOnly:
                ((Action<TEntity, DateOnly>)_setValue)(entity, copied.GetDateOnly(_field));
                break;
            case LatticeFieldTypeEnum.TimeOnly:
                ((Action<TEntity, TimeOnly>)_setValue)(entity, copied.GetTimeOnly(_field));
                break;
            case LatticeFieldTypeEnum.TimeSpan:
                ((Action<TEntity, TimeSpan>)_setValue)(entity, copied.GetTimeSpan(_field));
                break;
            case LatticeFieldTypeEnum.String:
                ((Action<TEntity, string>)_setValue)(entity, (string)values[Slot]!);
                break;
            case LatticeFieldTypeEnum.Binary:
                ((Action<TEntity, byte[]>)_setValue)(entity, (byte[])values[Slot]!);
                break;
            default:
                throw new UnreachableException($"The field {_field.Name} is {_type}, which the engine does not store, or a nested field, which SetNested sets.");
        }
    }

    // Under the protection of the root: makes the value of a nested field into its slot, in the form a
    // record holds it. Null for a null value.
    public void CopyNested(in RootReader root, object?[] values) => values[Slot] = root.GetNested(_field);

    // After the protection: sets the property of a nested field to what FromRecord makes of its slot, or
    // to null.
    public void SetNested(TEntity entity, object?[] values)
    {
        if (values[Slot] is { } value)
        {
            ((Action<TEntity, object>)_setValue)(entity, value);

            return;
        }

        _setNull!(entity);
    }
}
