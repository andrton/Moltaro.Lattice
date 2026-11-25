using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal static class TestScalarView
{
    public static object? Read(LatticeValueView view)
    {
        if (view.IsNull)
        {
            return null;
        }

        return view.Declaration.ScalarType switch
        {
            LatticeFieldTypeEnum.Boolean => view.Get<bool>(),
            LatticeFieldTypeEnum.Int8 => view.Get<sbyte>(),
            LatticeFieldTypeEnum.UInt8 => view.Get<byte>(),
            LatticeFieldTypeEnum.Int16 => view.Get<short>(),
            LatticeFieldTypeEnum.UInt16 => view.Get<ushort>(),
            LatticeFieldTypeEnum.Int32 => view.Get<int>(),
            LatticeFieldTypeEnum.UInt32 => view.Get<uint>(),
            LatticeFieldTypeEnum.Int64 => view.Get<long>(),
            LatticeFieldTypeEnum.UInt64 => view.Get<ulong>(),
            LatticeFieldTypeEnum.Float32 => view.Get<float>(),
            LatticeFieldTypeEnum.Float64 => view.Get<double>(),
            LatticeFieldTypeEnum.Decimal => view.Get<decimal>(),
            LatticeFieldTypeEnum.Guid => view.Get<Guid>(),
            LatticeFieldTypeEnum.DateOnly => view.Get<DateOnly>(),
            LatticeFieldTypeEnum.TimeOnly => view.Get<TimeOnly>(),
            LatticeFieldTypeEnum.TimeSpan => view.Get<TimeSpan>(),
            LatticeFieldTypeEnum.DateTimeOffset => view.Get<DateTimeOffset>(),
            LatticeFieldTypeEnum.String => view.GetString(),
            LatticeFieldTypeEnum.Binary => Bytes(view),
            _ => throw new InvalidOperationException("The context requires a scalar declaration."),
        };

        static byte[] Bytes(LatticeValueView bytes)
        {
            var result = new byte[bytes.ByteLength];
            var offset = 0;
            for (var part = 0; part < bytes.PartCount; part++)
            {
                var slice = bytes.GetPart(part);
                slice.CopyTo(result.AsSpan(offset));
                offset += slice.Length;
            }

            Assert.Equal(result.Length, offset);
            return result;
        }
    }
}
