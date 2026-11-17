using System.Diagnostics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Sets;

// A primary key given to a set, turned into the key of the index of its entity type (EntityKey) by
// the rule of the contract (ILatticeSet<TEntity>, remarks; brief of S07): the key type the schema
// declares, or for an integer key an integer of any width whose value fits the key type, an unsigned
// key type taking no negative value. A Guid key is a Guid and nothing else, a String key a string
// and nothing else: no text is parsed. A String key that is not well-formed text is refused as the
// core refuses it (the planner's answer to Q41). Anything else is an ArgumentException with the
// message of the check of the core, FieldValues.NormalizeKey, which refuses exactly what is refused
// here, so a key is refused alike on both implementations.
//
// A key is converted without boxing it when it is given as its own type or as an integer of another
// width, through a class or by name, and the hash of a String key is computed here, before the read
// enters the protection.
// An entity type the engine does not store converts its key all the same, and nothing is looked up
// with it.
internal static class KeyArgument
{
    // A key given as a value of its own type, through a class or by name.
    public static EntityKey Of<TKey>(EntityTable table, TKey key)
        where TKey : notnull
    {
        switch (table.KeyKind)
        {
            case KeyKindEnum.Integer when IntegerOf(key) is { } value && Fits(table, value):
                return Integer(table, value);

            case KeyKindEnum.Guid when typeof(TKey) == typeof(Guid):
                return EntityKey.OfGuid(Unsafe.As<TKey, Guid>(ref key));

            case KeyKindEnum.Text when typeof(TKey) == typeof(string):
                // A reference type: the conversion through object is no box.
                return Text(table, (string)(object)key);

            default:
                // A key of another type, or one outside the range of the key type: the box is the
                // cost of a key that is about to be refused. A key given as an object is one
                // already, and is not boxed again.
                return Of(table, (object)key);
        }
    }

    // A key given as an object, which holds its value boxed: one the caller held as an object, or one
    // of a type that the generic form does not convert.
    public static EntityKey Of(EntityTable table, object key)
    {
        ArgumentNullException.ThrowIfNull(key);

        switch (table.KeyKind)
        {
            case KeyKindEnum.Integer when IntegerOf(key) is { } value && Fits(table, value):
                return Integer(table, value);

            case KeyKindEnum.Guid when key is Guid guid:
                return EntityKey.OfGuid(guid);

            case KeyKindEnum.Text when key is string text:
                return Text(table, text);
        }

        // The check of the core refuses the key with its message: it accepts no key that is refused
        // above.
        FieldValues.NormalizeKey(table.EntityType, key);

        throw new UnreachableException($"The core accepted the key '{key}' for {table.EntityType.Name}, which the engine refused.");
    }

    // The key among the values a write took of an object or a record, which the core checked: the
    // value of the type the key field declares, and for a String well-formed text.
    public static EntityKey OfValues(EntityTable table, object?[] values)
    {
        var key = values[table.EntityType.Key.Ordinal]!;

        return table.KeyKind switch
        {
            KeyKindEnum.Integer => Integer(table, IntegerOf(key)!.Value),
            KeyKindEnum.Guid => EntityKey.OfGuid((Guid)key),
            _ => EntityKey.OfText((string)key, table.TextHash((string)key)),
        };
    }

    // The value of a key given as one of the eight integer types, read as its own type without a box;
    // null for a key of any other type.
    private static Int128? IntegerOf<TKey>(TKey key)
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

        if (typeof(TKey) == typeof(ulong))
        {
            return Unsafe.As<TKey, ulong>(ref key);
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

        return null;
    }

    // The value of a boxed key of one of the eight integer types; null for a key of any other type.
    private static Int128? IntegerOf(object key) => key switch
    {
        long value => value,
        int value => value,
        short value => value,
        sbyte value => value,
        ulong value => value,
        uint value => value,
        ushort value => value,
        byte value => value,
        _ => null,
    };

    // Whether an integer is a value of the key type: one of another width that fits finds its entity,
    // and one that does not fit is refused, never cut or wrapped to the width.
    private static bool Fits(EntityTable table, Int128 value) => IntegerRange.Of(table.EntityType.Key.Declaration.Type).Contains(value);

    // An integer key of the key type, as EntityKey holds it: the bits of a UInt64 as a long, and every
    // other value as itself.
    private static EntityKey Integer(EntityTable table, Int128 value)
    {
        var type = table.EntityType.Key.Declaration.Type;

        return EntityKey.OfInteger(type, type == LatticeFieldTypeEnum.UInt64 ? unchecked((long)(ulong)value) : (long)value);
    }

    // A String key given as itself: text that is not well-formed is an ArgumentException, as the
    // core refuses it (the planner's answer to Q41); a check of one pass that allocates nothing, and the
    // hash under the comparison of the key.
    private static EntityKey Text(EntityTable table, string text)
    {
        ArgumentNullException.ThrowIfNull(text, "key");
        FieldValues.ThrowIfNotWellFormed(table.EntityType.Key, text, "key");

        return EntityKey.OfText(text, table.TextHash(text));
    }
}
