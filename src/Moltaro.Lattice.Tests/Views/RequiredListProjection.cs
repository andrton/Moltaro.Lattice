namespace Moltaro.Lattice.Tests.Views;

[LatticeView(typeof(ByteProjectionModel))]
[LatticeViewField(nameof(ByteProjectionModel.Data), LatticeFieldTypeEnum.Binary)]
[LatticeViewField(nameof(ByteProjectionModel.Numbers), IsNullable = false)]
internal readonly ref partial struct RequiredListProjection;
