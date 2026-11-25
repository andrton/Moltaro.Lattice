namespace Moltaro.Lattice.Tests.Views;

[LatticeView(typeof(ByteProjectionModel))]
[LatticeViewField(nameof(ByteProjectionModel.Data), LatticeFieldTypeEnum.Binary)]
internal readonly ref partial struct BinaryProjection;
