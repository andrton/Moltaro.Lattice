namespace Moltaro.Lattice.Tests.Views;

[LatticeView(typeof(ByteProjectionModel))]
[LatticeViewField(nameof(ByteProjectionModel.Data), LatticeFieldTypeEnum.List, IsNullable = false)]
internal readonly ref partial struct ListProjection;
