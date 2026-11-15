using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Values;

// Whether the integer a write holds at an Enum position is a value its enum type allows (Q03.5,
// Q03.6; brief of Epic 1, sprint S07): LatticeEnumType.IsValid is the rule. The validation of the core
// asks it at a write and when an operation is added to a batch, at every depth, for a record
// (RecordValues) and for an object (ClassBinder), with every other validation of a value, so a value
// that is not allowed is ValidationFailed on every implementation before the conditions of the write
// and before a refusal as not implemented (Q25). A value set into a record is not asked about; the
// write is.
//
// The integer is boxed, as a record holds it and as a bound class gives it, in the underlying type of
// the enum type, which the caller has checked; it is unboxed here, so asking allocates nothing and
// runs no reflection.
internal static class EnumValues
{
    public static bool IsValid(LatticeEnumType enumType, object integer) => enumType.IsValid(integer switch
    {
        sbyte value => value,
        byte value => value,
        short value => value,
        ushort value => value,
        int value => value,
        uint value => value,
        long value => value,
        ulong value => value,
        _ => throw new UnreachableException($"An Enum value is held as an integer, not as a {integer.GetType().Name}."),
    });

    // What a message of the validation says of an integer the enum type does not allow: the integer,
    // and for a Flags type that a bit of it is declared by no member.
    public static string Undeclared(LatticeEnumType enumType, object integer) => enumType.IsFlags
        ? string.Create(CultureInfo.InvariantCulture, $"the value {integer}, which has a bit that no member of the Flags enum type '{enumType.Name}' declares")
        : string.Create(CultureInfo.InvariantCulture, $"the value {integer}, which the enum type '{enumType.Name}' does not declare");
}
