namespace Moltaro.Lattice.Tests.Views;

[LatticeView(typeof(MembershipItem))]
[LatticeViewField(nameof(MembershipItem.DisplayOrder), Ignore = true)]
[LatticeViewField("Home.DisplayOrder", Ignore = true)]
internal readonly ref partial struct MembershipView;
