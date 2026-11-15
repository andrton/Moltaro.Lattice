using System.Diagnostics;
using System.Linq.Expressions;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// How a read sets one field of an object of a bound class from the root it reads: a setter compiled
// once, when the set is made, that takes the value in the .NET type of the field, so that a value
// is never boxed, and for a nullable field one that sets it to null. Both call the set accessor C#
// sees for the property (BoundProperty.SetAccessor), of any accessibility, virtually. No reflection
// runs when a value is set.
internal readonly struct ClassField<TEntity>
    where TEntity : class
{
    private readonly LatticeField _field;

    private readonly LatticeFieldTypeEnum _type;

    // An Action<TEntity, T> for the type T a record holds for the field (ValueType), which converts
    // to the type of the property, a Nullable<T> for a nullable value type.
    private readonly Delegate _setValue;

    private readonly Action<TEntity>? _setNull;

    private ClassField(LatticeField field, Delegate setValue, Action<TEntity>? setNull)
    {
        _field = field;
        _type = field.Declaration.Type;
        _setValue = setValue;
        _setNull = setNull;
    }

    // The setters of a property of a field the engine stores: a scalar that is not an Enum, a String
    // or a Binary.
    public static ClassField<TEntity> Compile(BoundProperty property)
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

        return new ClassField<TEntity>(property.Field, setValue, setNull);
    }

    // Sets the property from the root the reader reads. The read holds the protection of the root.
    public void Set(TEntity entity, in RootReader reader)
    {
        if (_setNull is not null && reader.IsNull(_field))
        {
            _setNull(entity);

            return;
        }

        switch (_type)
        {
            case LatticeFieldTypeEnum.Boolean:
                ((Action<TEntity, bool>)_setValue)(entity, reader.GetBoolean(_field));
                break;
            case LatticeFieldTypeEnum.Int8:
                ((Action<TEntity, sbyte>)_setValue)(entity, (sbyte)reader.GetInteger(_field));
                break;
            case LatticeFieldTypeEnum.Int16:
                ((Action<TEntity, short>)_setValue)(entity, (short)reader.GetInteger(_field));
                break;
            case LatticeFieldTypeEnum.Int32:
                ((Action<TEntity, int>)_setValue)(entity, (int)reader.GetInteger(_field));
                break;
            case LatticeFieldTypeEnum.Int64:
                ((Action<TEntity, long>)_setValue)(entity, reader.GetInteger(_field));
                break;
            case LatticeFieldTypeEnum.UInt8:
                ((Action<TEntity, byte>)_setValue)(entity, (byte)reader.GetUnsigned(_field));
                break;
            case LatticeFieldTypeEnum.UInt16:
                ((Action<TEntity, ushort>)_setValue)(entity, (ushort)reader.GetUnsigned(_field));
                break;
            case LatticeFieldTypeEnum.UInt32:
                ((Action<TEntity, uint>)_setValue)(entity, (uint)reader.GetUnsigned(_field));
                break;
            case LatticeFieldTypeEnum.UInt64:
                ((Action<TEntity, ulong>)_setValue)(entity, reader.GetUnsigned(_field));
                break;
            case LatticeFieldTypeEnum.Float32:
                ((Action<TEntity, float>)_setValue)(entity, reader.GetFloat32(_field));
                break;
            case LatticeFieldTypeEnum.Float64:
                ((Action<TEntity, double>)_setValue)(entity, reader.GetFloat64(_field));
                break;
            case LatticeFieldTypeEnum.Decimal:
                ((Action<TEntity, decimal>)_setValue)(entity, reader.GetDecimal(_field));
                break;
            case LatticeFieldTypeEnum.Guid:
                ((Action<TEntity, Guid>)_setValue)(entity, reader.GetGuid(_field));
                break;
            case LatticeFieldTypeEnum.DateTimeOffset:
                ((Action<TEntity, DateTimeOffset>)_setValue)(entity, reader.GetDateTimeOffset(_field));
                break;
            case LatticeFieldTypeEnum.DateOnly:
                ((Action<TEntity, DateOnly>)_setValue)(entity, reader.GetDateOnly(_field));
                break;
            case LatticeFieldTypeEnum.TimeOnly:
                ((Action<TEntity, TimeOnly>)_setValue)(entity, reader.GetTimeOnly(_field));
                break;
            case LatticeFieldTypeEnum.TimeSpan:
                ((Action<TEntity, TimeSpan>)_setValue)(entity, reader.GetTimeSpan(_field));
                break;
            case LatticeFieldTypeEnum.String:
                ((Action<TEntity, string>)_setValue)(entity, reader.GetString(_field));
                break;
            case LatticeFieldTypeEnum.Binary:
                ((Action<TEntity, byte[]>)_setValue)(entity, reader.GetBinary(_field));
                break;
            default:
                throw new UnreachableException($"The field {_field.Name} is {_type}, which the engine does not store.");
        }
    }
}
