namespace Moltaro.Lattice.Tests.Indexes;

// The schema of the tests of the search by the full key of an index, and its entity type Member, bound to
// the class Member:
// - Id, the key, an Int64; Group, an Int32; Login, a String compared without case; Code, a nullable Guid;
//   Joined, a nullable DateOnly; Note, a nullable String in no index.
// - ByGroup (Group), one field; ByGroupAndLogin (Group, Login), two; ByAllFour (Group, Login, Code,
//   Joined), the most values a key takes, two of them nullable; ByCode (Code), one nullable field.
internal static class MemberSchema
{
    public static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Member", "ClrType": "{{typeof(Member).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Group", "Type": "Int32" },
                { "Id": 3, "Name": "Login", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 4, "Name": "Code", "Type": "Guid", "Nullable": true },
                { "Id": 5, "Name": "Joined", "Type": "DateOnly", "Nullable": true },
                { "Id": 6, "Name": "Note", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByGroup", "Fields": [2] },
                { "Id": 2, "Name": "ByGroupAndLogin", "Fields": [2, 3] },
                { "Id": 3, "Name": "ByAllFour", "Fields": [2, 3, 4, 5] },
                { "Id": 4, "Name": "ByCode", "Fields": [4] }
              ]
            }
          ]
        }
        """);

    public static ValueTask<ILatticeDatabase> OpenAsync() => TestData.OpenMasterAsync(schema: Schema);

    // The ids of the members a search through a class found, in ascending order: a search returns
    // entities of one key in no order the contract states, so a test compares them as a set.
    public static long[] IdsOf(IEnumerable<Member> found) => [.. found.Select(member => member.Id).Order()];

    // The same for the records a search by name found.
    public static long[] IdsOf(IEnumerable<LatticeRecord> found) => [.. found.Select(record => record.Get<long>("Id")).Order()];

    public static LatticeRecord RecordOf(ILatticeSet members, Member member) => new(members.EntityType)
    {
        ["Id"] = member.Id,
        ["Group"] = member.Group,
        ["Login"] = member.Login,
        ["Code"] = member.Code,
        ["Joined"] = member.Joined,
        ["Note"] = member.Note,
    };
}
