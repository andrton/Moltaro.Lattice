using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Updates;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Text;

// A text operand of a forwarded update whose UTF-8 is split across two pieces of a transfer buffer:
// a four-byte character starts in the last byte of the first piece. It reads back as the text it
// was, and a bad continuation byte or a truncated character is refused as a validation failure.
public sealed class SplitTextOperandTests
{
    private static readonly LatticeSchema Schema = ItemSchema.Compile("{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }");

    [Fact]
    public void A_text_split_across_transfer_pieces_reads_back_and_a_bad_or_truncated_one_is_refused()
    {
        var text = string.Concat(Enumerable.Repeat("a\U0001F600", 50_000));
        var wire = new byte[sizeof(uint) + Encoding.UTF8.GetByteCount(text)];
        BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)(wire.Length - sizeof(uint)));
        Encoding.UTF8.GetBytes(text, wire.AsSpan(sizeof(uint)));
        Assert.Equal(0xF0, wire[TransferBuffer.PieceSize - 1]);
        Assert.InRange(wire[TransferBuffer.PieceSize], 0x80, 0xBF);

        Assert.True(Schema.TryGetEntityType(ItemSchema.EntityTypeName, out var type));
        var layout = StructureLayout.OfRoot(type, []);
        var position = layout.PositionOf([type.GetField("Text")]);
        using var transfer = new TransferBuffer();
        transfer.Write(wire);
        var decoded = Read();
        Assert.NotSame(text, decoded);
        Assert.Equal(text, decoded);

        var goodContinuation = wire[TransferBuffer.PieceSize];
        wire[TransferBuffer.PieceSize] = (byte)'(';
        Reload(wire);
        Assert.Equal(LatticeErrorEnum.ValidationFailed, Assert.Throws<LatticeException>(() => Read()).Error);

        wire[TransferBuffer.PieceSize] = goodContinuation;
        BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)(wire.Length - sizeof(uint) - 1));
        Reload(wire.AsSpan(0, wire.Length - 1));
        Assert.Equal(LatticeErrorEnum.ValidationFailed, Assert.Throws<LatticeException>(() => Read()).Error);

        string Read()
        {
            var sequence = transfer.Sequence;
            Assert.False(sequence.IsSingleSegment);
            var reader = new SequenceReader<byte>(sequence);
            var result = UpdateOperands.Read(ref reader, type, layout, position);
            Assert.True(reader.End);
            return Assert.IsType<string>(result);
        }

        void Reload(ReadOnlySpan<byte> value)
        {
            transfer.Clear();
            transfer.Write(value);
        }
    }
}
