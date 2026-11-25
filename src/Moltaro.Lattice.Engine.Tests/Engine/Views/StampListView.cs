using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

[LatticeView(typeof(StampSchema.Stamp))]
[LatticeViewField(nameof(StampSchema.Stamp.Data), LatticeFieldTypeEnum.List)]
internal readonly ref partial struct StampListView;
