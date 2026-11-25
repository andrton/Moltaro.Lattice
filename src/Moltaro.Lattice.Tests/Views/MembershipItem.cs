namespace Moltaro.Lattice.Tests.Views;

internal sealed class MembershipItem : MembershipKey
{
    public int DisplayOrder { get; set; }

    public MembershipAddress Home { get; set; } = new();
}
