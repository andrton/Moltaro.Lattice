using Moltaro.Lattice.Tests.NestedValues;

namespace Moltaro.Lattice.Tests.Views;

[LatticeView(typeof(Person))]
[LatticeViewField(nameof(Person.Photo), LatticeFieldTypeEnum.Binary)]
internal readonly ref partial struct PersonView;
