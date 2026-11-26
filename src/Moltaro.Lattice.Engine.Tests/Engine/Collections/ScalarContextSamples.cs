using System.Globalization;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed record ScalarContextSamples(string Declaration, string Enums, string Target, bool HasEquality, object[] Values)
{
    public static IReadOnlyList<(string Family, LatticeFieldTypeEnum Type)> Catalog { get; } =
    [
        .. Enum.GetValues<LatticeFieldTypeEnum>().Where(type => type <= LatticeFieldTypeEnum.Binary).Select(type => ("Scalar", type)),
        ("Dictionary", LatticeFieldTypeEnum.String),
        .. Enum.GetValues<LatticeFieldTypeEnum>().Where(FieldTypes.IsInteger).Select(type => ("Enum", type)),
        .. Enum.GetValues<LatticeFieldTypeEnum>().Where(FieldTypes.IsInteger).Select(type => ("Flags", type)),
        .. Enum.GetValues<LatticeFieldTypeEnum>().Where(FieldTypes.CanBeKey).Select(type => ("Reference", type)),
    ];

    public static ScalarContextSamples Of(string family, LatticeFieldTypeEnum type, string encoding)
    {
        if (family is "Enum" or "Flags")
        {
            return Enumeration(type, family == "Flags");
        }

        var values = type switch
        {
            LatticeFieldTypeEnum.String => Texts(encoding),
            LatticeFieldTypeEnum.Binary => Binaries(),
            _ => Ordinary(type),
        };
        if (family == "Reference")
        {
            var target = $$"""
                ,{ "Id": 2, "Name": "Target", "Fields": [{ "Id": 1, "Name": "Id", "Type": "{{type}}" }], "PrimaryKey": [1] }
                """;
            return new("\"Type\": \"Reference\", \"EntityType\": \"Target\"", "", target, true, values);
        }

        var storage = family == "Dictionary" ? ", \"StringStorage\": \"Dictionary\"" : "";
        return new($"\"Type\": \"{type}\"{storage}", "", "", FieldTypes.HasEquality(type), values);
    }

    private static ScalarContextSamples Enumeration(LatticeFieldTypeEnum type, bool flags)
    {
        var (first, second, combined) = type switch
        {
            LatticeFieldTypeEnum.Int8 => ((object)sbyte.MinValue, (object)(sbyte)1, (object)(sbyte)(sbyte.MinValue + 1)),
            LatticeFieldTypeEnum.UInt8 => ((object)(byte)128, (object)(byte)1, (object)(byte)129),
            LatticeFieldTypeEnum.Int16 => ((object)short.MinValue, (object)(short)1, (object)(short)(short.MinValue + 1)),
            LatticeFieldTypeEnum.UInt16 => ((object)(ushort)0x8000, (object)(ushort)1, (object)(ushort)0x8001),
            LatticeFieldTypeEnum.Int32 => ((object)int.MinValue, (object)1, (object)(int.MinValue + 1)),
            LatticeFieldTypeEnum.UInt32 => ((object)0x8000_0000U, (object)1U, (object)0x8000_0001U),
            LatticeFieldTypeEnum.Int64 => ((object)long.MinValue, (object)1L, (object)(long.MinValue + 1)),
            LatticeFieldTypeEnum.UInt64 => ((object)0x8000_0000_0000_0000UL, (object)1UL, (object)0x8000_0000_0000_0001UL),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        var zero = Convert.ChangeType(0, first.GetType(), CultureInfo.InvariantCulture)!;
        var members = flags ? [first, second] : Ordinary(type);
        var declarations = string.Join(
            ", ",
            members.Select((value, ordinal) => $$"""
                { "Name": "Member{{ordinal}}", "Value": {{Convert.ToString(value, CultureInfo.InvariantCulture)}} }
                """));
        var isFlags = flags ? "true" : "false";
        var types = $$"""
            { "Id": 1, "Name": "Marker", "UnderlyingType": "{{type}}", "Flags": {{isFlags}}, "Members": [{{declarations}}] }
            """;
        object[] values = flags ? [zero, first, second, combined] : members;
        return new("\"Type\": \"Enum\", \"Enum\": \"Marker\"", types, "", true, values);
    }

    private static object[] Ordinary(LatticeFieldTypeEnum type)
    {
        var item = EveryType.Compile().EntityTypes[0];
        var values = new List<object>();
        for (var ordinal = 0; ordinal < EveryTypeValues.Entities; ordinal++)
        {
            var value = EveryTypeValues.RecordOf(item, ordinal)[type.ToString()]!;
            if (!values.Any(present => EveryTypeValues.Same(present, value)))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static object[] Texts(string encoding)
    {
        var unit = encoding == "Utf8" ? 1 : 2;
        return ["", "aé€𐐨", .. AtBoundaries(unit).Select(length => new string('x', length / unit))];
    }

    private static object[] Binaries() =>
        [
            Array.Empty<byte>(),
            .. AtBoundaries(unit: 1).Select(length => Enumerable.Range(0, length).Select(ordinal => (byte)(ordinal * 47 + length)).ToArray()),
        ];

    private static IEnumerable<int> AtBoundaries(int unit)
    {
        int[] limits = [ValueBlocks.InlineLimit, ValueBlocks.SingleBlockLimit, ValueBlocks.ChunkPayload];
        foreach (var limit in limits)
        {
            yield return limit - unit;
            yield return limit;
            yield return limit + unit;
        }
    }
}
