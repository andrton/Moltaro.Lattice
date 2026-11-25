using System.Reflection;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Binding;

// The one place where a .NET enum is compared with an enum type of the schema, once, when a
// class is bound, for a property and for an element, a key or a value of a collection alike.
// They agree as a class agrees with its entity type, or the class does not bind: the same
// underlying type, [Flags] exactly when the enum type is a Flags type, and the same members,
// every name with its value, in both directions. Nothing is mapped or guessed, so a schema
// that changed without the class is found here and not when a value is read. A value with two
// names therefore has both names in the schema, and the schema says which of them is
// canonical.
internal static class DotNetEnumAgreement
{
    // How the .NET enum differs from the enum type; empty when they agree.
    public static List<string> Differences(Type dotNetEnum, LatticeEnumType enumType)
    {
        var underlying = Enum.GetUnderlyingType(dotNetEnum);
        var matches = underlying == FieldTypes.ValueType(enumType.UnderlyingType);
        var flags = dotNetEnum.IsDefined(typeof(FlagsAttribute), inherit: false);
        var members = dotNetEnum.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (field.Name, field.GetRawConstantValue()!));
        return Differences(enumType, matches, underlying.Name, flags, members);
    }

    public static List<string> Differences(LatticeEnumType enumType, bool matchesUnderlying, string underlyingName, bool isFlags,
        IEnumerable<(string Name, object Value)> members)
    {
        var stored = FieldTypes.ValueType(enumType.UnderlyingType);
        var differences = new List<string>();

        // Values of another integer type are read as other values, so only compare their types.
        if (!matchesUnderlying)
        {
            differences.Add($"its underlying type is {underlyingName}, and the enum type is stored as {stored.Name}");
        }
        else
        {
            var declaredValues = enumType.Members.ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
            var namesOfDotNetEnum = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                var value = Int128Of(member.Value);
                namesOfDotNetEnum.Add(member.Name);

                if (!declaredValues.TryGetValue(member.Name, out var declaredValue))
                {
                    differences.Add($"it names {member.Name} = {value}, which the enum type does not declare");
                }
                else if (declaredValue != value)
                {
                    differences.Add($"it names {member.Name} = {value}, which the enum type declares as {declaredValue}");
                }
            }

            foreach (var member in enumType.Members)
            {
                if (!namesOfDotNetEnum.Contains(member.Name))
                {
                    differences.Add($"it has no {member.Name}, which the enum type declares as {member.Value}");
                }
            }
        }

        if (isFlags != enumType.IsFlags)
        {
            differences.Add(enumType.IsFlags
                ? "it has no [Flags], and the enum type is a Flags type"
                : "it has [Flags], and the enum type is not a Flags type");
        }

        return differences;
    }

    // The value of a member of a .NET enum, in its underlying integer type.
    private static Int128 Int128Of(object value) => value switch
    {
        sbyte number => number,
        byte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number => number,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
