using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// The schema of the tests of the counters of the engine, and the class of its entity type Piece.
//
// Piece has the key Id, an Int64; Name, a String; Note, a nullable String; Data, a nullable Binary;
// and Count, an Int32. By the formula of the brief of S05 ("The fixed part") its fixed part is the
// 32 bytes of the header, a null bitmap of one byte for the two nullable fields, the 8 bytes of Id,
// one descriptor of 16 bytes for each of the three String and Binary fields and the 4 bytes of
// Count: 93 bytes. Holder has the key Id and a List field: the engine cannot store it yet.
internal static class PieceSchema
{
    public const int FixedSize = 32 + 1 + 8 + (3 * 16) + 4;

    public static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Piece", "ClrType": "{{typeof(Piece).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Name", "Type": "String" },
                { "Id": 3, "Name": "Note", "Type": "String", "Nullable": true },
                { "Id": 4, "Name": "Data", "Type": "Binary", "Nullable": true },
                { "Id": 5, "Name": "Count", "Type": "Int32" }
              ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Holder",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static Task<EngineDatabase> OpenAsync() => EngineMaster.OpenAsync(Schema);

    // A piece whose String values are ASCII, one byte for every character in UTF-8, and whose values
    // have the given lengths; null for a value that is null.
    public static Piece Of(long id, int name, int? note = null, int? data = null) => new()
    {
        Id = id,
        Name = new string('n', name),
        Note = note is { } noteLength ? new string('o', noteLength) : null,
        Data = data is { } dataLength ? new byte[dataLength] : null,
        Count = (int)id,
    };

    // The same values as a record of Piece.
    public static LatticeRecord RecordOf(LatticeEntityType piece, long id, int name, int? note = null, int? data = null)
    {
        var values = Of(id, name, note, data);

        return new LatticeRecord(piece)
        {
            ["Id"] = values.Id,
            ["Name"] = values.Name,
            ["Note"] = values.Note,
            ["Data"] = values.Data,
            ["Count"] = values.Count,
        };
    }

    public sealed class Piece
    {
        public long Id { get; set; }

        public string Name { get; set; } = "";

        public string? Note { get; set; }

        public byte[]? Data { get; set; }

        public int Count { get; set; }
    }
}
