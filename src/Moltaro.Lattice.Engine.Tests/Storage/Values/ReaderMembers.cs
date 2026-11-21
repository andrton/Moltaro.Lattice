using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// Every Get… member of RootReader, and GetLength, with the fields each serves (brief of S05,
// "Which member serves which field"): the same as the Set… member of its name, see WriterMembers,
// and GetLength a String and a Binary field. IsNull serves every field and is not among them. A
// call gives the reader by value, so that a test can make the reader inside the lambda of
// Assert.Throws, which cannot capture a ref struct.
internal static class ReaderMembers
{
    public static readonly IReadOnlyList<(string Name, Action<RootReader, LatticeField> Get, Func<LatticeValueDeclaration, bool> Serves)> Getters =
    [
        ("GetBoolean", (reader, field) => reader.GetBoolean(field), WriterMembers.Is(LatticeFieldTypeEnum.Boolean)),
        ("GetInteger", (reader, field) => reader.GetInteger(field), WriterMembers.IsSigned),
        ("GetUnsigned", (reader, field) => reader.GetUnsigned(field), WriterMembers.IsUnsigned),
        ("GetFloat32", (reader, field) => reader.GetFloat32(field), WriterMembers.Is(LatticeFieldTypeEnum.Float32)),
        ("GetFloat64", (reader, field) => reader.GetFloat64(field), WriterMembers.Is(LatticeFieldTypeEnum.Float64)),
        ("GetDecimal", (reader, field) => reader.GetDecimal(field), WriterMembers.Is(LatticeFieldTypeEnum.Decimal)),
        ("GetGuid", (reader, field) => reader.GetGuid(field), WriterMembers.Is(LatticeFieldTypeEnum.Guid)),
        ("GetDateTimeOffset", (reader, field) => reader.GetDateTimeOffset(field), WriterMembers.Is(LatticeFieldTypeEnum.DateTimeOffset)),
        ("GetDateOnly", (reader, field) => reader.GetDateOnly(field), WriterMembers.Is(LatticeFieldTypeEnum.DateOnly)),
        ("GetTimeOnly", (reader, field) => reader.GetTimeOnly(field), WriterMembers.Is(LatticeFieldTypeEnum.TimeOnly)),
        ("GetTimeSpan", (reader, field) => reader.GetTimeSpan(field), WriterMembers.Is(LatticeFieldTypeEnum.TimeSpan)),
        ("GetString", (reader, field) => reader.GetString(field), WriterMembers.Is(LatticeFieldTypeEnum.String)),
        ("GetBinary", (reader, field) => reader.GetBinary(field), WriterMembers.Is(LatticeFieldTypeEnum.Binary)),
        ("GetLength", (reader, field) => reader.GetLength(field), declaration => declaration.Type is LatticeFieldTypeEnum.String or LatticeFieldTypeEnum.Binary),
    ];
}
