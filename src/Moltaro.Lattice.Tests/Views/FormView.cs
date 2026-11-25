using Moltaro.Lattice.Tests.NestedValues.Copies;

namespace Moltaro.Lattice.Tests.Views;

[LatticeView(typeof(Form))]
[LatticeViewField("Main.Data", LatticeFieldTypeEnum.Binary)]
[LatticeViewField("Spare.Data", LatticeFieldTypeEnum.Binary)]
internal readonly ref partial struct FormView;
