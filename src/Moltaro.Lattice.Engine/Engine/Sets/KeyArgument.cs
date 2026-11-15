using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Sets;

// A primary key given to a set, turned into the key of the index of its entity type by the rule of
// the contract: the key type the schema declares, or an integer of another width when it fits;
// anything else is an ArgumentException (ILatticeSet<TEntity>, remarks), and so is a String key
// that is not well-formed text. A key of a type the index takes is converted without boxing it, and
// a String key is checked for well-formed text as the core checks it; any other value goes through
// the check of the core, FieldValues.NormalizeKey, which accepts what fits and refuses the rest with
// its message. For an entity type the engine does not store, whose key kind is Other, the key is
// only checked.
internal static class KeyArgument
{
    // A key given through a class, as a value of its own type.
    public static EntityKey Of<TKey>(EntityTable table, TKey key)
        where TKey : notnull
    {
        switch (table.KeyKind)
        {
            case KeyKindEnum.Number:
                return new EntityKey(Number(table, key));

            case KeyKindEnum.Text when typeof(TKey) == typeof(string):
                // A reference type: the conversion through object is no box.
                return Text(table, (string)(object)key);

            case KeyKindEnum.Text:
                return new EntityKey((string)FieldValues.NormalizeKey(table.EntityType, key));

            default:
                return Checked(table, key);
        }
    }

    // A key given by name, as an object; the caller boxed it.
    public static EntityKey Of(EntityTable table, object key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return table.KeyKind switch
        {
            KeyKindEnum.Number => new EntityKey(key switch
            {
                long value => value,
                int value => value,
                short value => value,
                sbyte value => value,
                uint value => value,
                ushort value => value,
                byte value => value,
                _ => (long)FieldValues.NormalizeKey(table.EntityType, key),
            }),
            KeyKindEnum.Text => key is string text ? Text(table, text) : new EntityKey((string)FieldValues.NormalizeKey(table.EntityType, key)),
            _ => Checked(table, key),
        };
    }

    // The key among the values a write took of an object or a record, which the core checked.
    public static EntityKey OfValues(EntityTable table, object?[] values)
    {
        var key = values[table.EntityType.Key.Ordinal]!;

        return table.KeyKind switch
        {
            KeyKindEnum.Number => new EntityKey((long)key),
            KeyKindEnum.Text => new EntityKey((string)key),
            _ => default,
        };
    }

    // An integer of a width that fits Int64 is read as its own type, without a box; any other
    // value, also a UInt64 beyond Int64, goes to the check of the core.
    private static long Number<TKey>(EntityTable table, TKey key)
        where TKey : notnull
    {
        if (typeof(TKey) == typeof(long))
        {
            return Unsafe.As<TKey, long>(ref key);
        }

        if (typeof(TKey) == typeof(int))
        {
            return Unsafe.As<TKey, int>(ref key);
        }

        if (typeof(TKey) == typeof(short))
        {
            return Unsafe.As<TKey, short>(ref key);
        }

        if (typeof(TKey) == typeof(sbyte))
        {
            return Unsafe.As<TKey, sbyte>(ref key);
        }

        if (typeof(TKey) == typeof(uint))
        {
            return Unsafe.As<TKey, uint>(ref key);
        }

        if (typeof(TKey) == typeof(ushort))
        {
            return Unsafe.As<TKey, ushort>(ref key);
        }

        if (typeof(TKey) == typeof(byte))
        {
            return Unsafe.As<TKey, byte>(ref key);
        }

        return (long)FieldValues.NormalizeKey(table.EntityType, key);
    }

    // A String key given as itself: text that is not well-formed is an ArgumentException, as the
    // core refuses it (the planner's answer to Q41); a check of one pass that allocates nothing.
    private static EntityKey Text(EntityTable table, string text)
    {
        ArgumentNullException.ThrowIfNull(text, "key");
        FieldValues.ThrowIfNotWellFormed(table.EntityType.Key, text, "key");

        return new EntityKey(text);
    }

    // Checks a key of an entity type the engine does not store; nothing is looked up with it.
    private static EntityKey Checked(EntityTable table, object key)
    {
        _ = FieldValues.NormalizeKey(table.EntityType, key);

        return default;
    }
}
