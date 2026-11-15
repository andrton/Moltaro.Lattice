using System.Diagnostics;
using System.Linq.Expressions;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

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
internal readonly struct ClassField<TEntity>
    where TEntity : class
{
    // The slot of a field that is no String and no Binary.
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
        _type = field.Declaration.Type;
        Slot = slot;
        _setValue = setValue;
        _setNull = setNull;
    }

    // Where a read keeps the value of a String or Binary field between the copy and the object;
    // NoSlot for any other field.
    public int Slot { get; }

    // The setters of a property of a field the engine stores: a scalar that is not an Enum, a String
    // or a Binary. A String or Binary field gets the given slot.
    public static ClassField<TEntity> Compile(BoundProperty property, int slot)
    {
        var declaration = property.Field.Declaration;
        var propertyType = property.Property.PropertyType;
        var entity = Expression.Parameter(typeof(TEntity), "entity");
        var value = Expression.Parameter(declaration.ValueType, "value");
        var converted = propertyType == declaration.ValueType ? (Expression)value : Expression.Convert(value, propertyType);

        var setValue = Expression
            .Lambda(typeof(Action<,>).MakeGenericType(typeof(TEntity), declaration.ValueType), Expression.Call(entity, property.SetAccessor, converted), entity, value)
            .Compile();

        var setNull = declaration.IsNullable
            ? Expression.Lambda<Action<TEntity>>(Expression.Call(entity, property.SetAccessor, Expression.Default(propertyType)), entity).Compile()
            : null;

        return new ClassField<TEntity>(property.Field, slot, setValue, setNull);
    }

    // Whether the field is a String or a Binary, whose value lies outside the fixed part of a root.
    public static bool HasSlot(LatticeField field) =>
        field.Declaration.Type is LatticeFieldTypeEnum.String or LatticeFieldTypeEnum.Binary;

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
                throw new UnreachableException($"The field {_field.Name} is {_type}, which the engine does not store.");
        }
    }
}
