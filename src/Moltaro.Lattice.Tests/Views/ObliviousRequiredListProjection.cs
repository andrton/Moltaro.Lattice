namespace Moltaro.Lattice.Tests.Views;

[LatticeView(typeof(ObliviousProjectionModel))]
[LatticeViewField(nameof(ObliviousProjectionModel.Numbers), IsNullable = false)]
internal readonly ref partial struct ObliviousRequiredListProjection;
