namespace Moltaro.Lattice.Tests.Indexes;

// Bound to the entity type "Member" of MemberSchema.
internal sealed class Member
{
    public long Id { get; set; }

    public int Group { get; set; }

    public string Login { get; set; } = "";

    public Guid? Code { get; set; }

    public DateOnly? Joined { get; set; }

    public string? Note { get; set; }
}
