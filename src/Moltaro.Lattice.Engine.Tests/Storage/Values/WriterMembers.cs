using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// Every Set… member of RootWriter, each with a value that every field it serves can hold, and the
// fields it serves (brief of S05, "Which member serves which field"): SetInteger the signed
// integers and an Enum of a signed underlying type, SetUnsigned the unsigned integers and an Enum
// of an unsigned underlying type, every other member the one field type of its name. SetNull
// serves every field and is not among them.
internal static class WriterMembers
{
    public static readonly IReadOnlyList<(string Name, Func<LatticeField, WriterAction> Set, Func<LatticeValueDeclaration, bool> Serves)> Setters =
    [
        ("SetBoolean", field => (ref RootWriter w) => w.SetBoolean(field, true), Is(LatticeFieldTypeEnum.Boolean)),
        ("SetInteger", field => (ref RootWriter w) => w.SetInteger(field, SignedValueOf(field)), IsSigned),
        ("SetUnsigned", field => (ref RootWriter w) => w.SetUnsigned(field, UnsignedValueOf(field)), IsUnsigned),
        ("SetFloat32", field => (ref RootWriter w) => w.SetFloat32(field, 1.5f), Is(LatticeFieldTypeEnum.Float32)),
        ("SetFloat64", field => (ref RootWriter w) => w.SetFloat64(field, 2.5), Is(LatticeFieldTypeEnum.Float64)),
        ("SetDecimal", field => (ref RootWriter w) => w.SetDecimal(field, 3.25m), Is(LatticeFieldTypeEnum.Decimal)),
        ("SetGuid", field => (ref RootWriter w) => w.SetGuid(field, Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")), Is(LatticeFieldTypeEnum.Guid)),
        ("SetDateTimeOffset", field => (ref RootWriter w) => w.SetDateTimeOffset(field, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)), Is(LatticeFieldTypeEnum.DateTimeOffset)),
        ("SetDateOnly", field => (ref RootWriter w) => w.SetDateOnly(field, new DateOnly(2026, 11, 15)), Is(LatticeFieldTypeEnum.DateOnly)),
        ("SetTimeOnly", field => (ref RootWriter w) => w.SetTimeOnly(field, new TimeOnly(12, 30)), Is(LatticeFieldTypeEnum.TimeOnly)),
        ("SetTimeSpan", field => (ref RootWriter w) => w.SetTimeSpan(field, TimeSpan.FromMinutes(90)), Is(LatticeFieldTypeEnum.TimeSpan)),
        ("SetString", field => (ref RootWriter w) => w.SetString(field, "text"), Is(LatticeFieldTypeEnum.String)),
        ("SetBinary", field => (ref RootWriter w) => w.SetBinary(field, [1, 2, 3]), Is(LatticeFieldTypeEnum.Binary)),
    ];

    // Sets the field to a valid value, through the one member that serves it.
    public static void SetValid(ref RootWriter writer, LatticeField field)
    {
        var setter = Setters.Single(member => member.Serves(field.Declaration));

        setter.Set(field)(ref writer);
    }

    public static bool IsSigned(LatticeValueDeclaration declaration) =>
        IntegerType(declaration) is LatticeFieldTypeEnum.Int8 or LatticeFieldTypeEnum.Int16 or LatticeFieldTypeEnum.Int32 or LatticeFieldTypeEnum.Int64;

    public static bool IsUnsigned(LatticeValueDeclaration declaration) =>
        IntegerType(declaration) is LatticeFieldTypeEnum.UInt8 or LatticeFieldTypeEnum.UInt16 or LatticeFieldTypeEnum.UInt32 or LatticeFieldTypeEnum.UInt64;

    public static Func<LatticeValueDeclaration, bool> Is(LatticeFieldTypeEnum type) => declaration => declaration.Type == type;

    // The integer type a value of the field is held as: the underlying type of an Enum, the declared
    // type of any other field.
    private static LatticeFieldTypeEnum IntegerType(LatticeValueDeclaration declaration) =>
        declaration.EnumType?.UnderlyingType ?? declaration.Type;

    // 1 for an integer field and the first declared member for an Enum field, so that the value is
    // valid for the field whatever its width (brief of S05: the tests of S05 use declared members).
    private static long SignedValueOf(LatticeField field) =>
        field.Declaration.EnumType is { } enumType ? (long)enumType.Members[0].Value : 1;

    private static ulong UnsignedValueOf(LatticeField field) =>
        field.Declaration.EnumType is { } enumType ? (ulong)enumType.Members[0].Value : 1;
}
