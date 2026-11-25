namespace Moltaro.Lattice.Tests.Views;

[LatticeView(typeof(ObliviousProjectionModel))]
[LatticeViewField(nameof(ObliviousProjectionModel.Numbers), IsNullable = true)]
internal readonly ref partial struct ObliviousOptionalListProjection;
