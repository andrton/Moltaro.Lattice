using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Replication.Forwarding;

// The bodies of a write forwarded through a child and of its reply
// (docs/design/milestone-1-engine-contract.md, "Replication transport and initial loading"). Every
// number is little-endian; a mark of presence is one byte, 0 or 1.
//
// A request: its kind (one byte, the value of Access.WriteKindEnum), the type id of its entity type
// (UInt32), the expected revision (0, or 1 and the two UInt64 of the token in the order it holds them;
// always 0 for an insert), the minimum position (0, or 1, its history and its sequence, each a UInt64),
// then the key, in the form of the key of a Delete of the stream (Engine.Entities.PrimaryKeys.WriteKey);
// a Delete ends there. An insert, a replace and an upsert go on with the length of the root (UInt64) and
// the root in the load format, as a Blocks frame carries it: type id (UInt32), last mutation sequence
// (UInt64), which the child writes as 0 and the master stamps when it prepares the write, then what
// Load.BlockExport writes. The key and the root are written and read by the engine with the entity type
// they belong to; here are the parts around them.
//
// A reply: 0 and a result — its position, history and sequence, and Changed (one byte, 0 or 1) — or 1 and
// a refusal: its error (Int32, LatticeErrorEnum), its message (the length of its UTF-8, UInt32, and that
// UTF-8), Limit (0, or 1 and LatticeLimitEnum as an Int32), OperationIndex (0, or 1 and an Int32),
// CommittedPosition (0, or 1, its history and its sequence) and the unique index (0, or 1 and the
// identity of the index, an Int32, which UniqueConstraintViolation always has and no other error has). The
// identity is that of the index within the entity type of the write (Schema.LatticeIndex.Id), the same on
// the child, whose schema the handshake bound to the master's, and the child gives the caller its name.
//
// Each Read… refuses a body that is truncated, that goes on after its end, or that holds a value no write
// of the engine makes, with ValidationFailed, before anything of it is used.
internal static class ForwardEncoding
{
    // The most bytes of UTF-8 one character takes.
    private const int MostBytesOfACharacter = 4;

    // The part of a request before its key.
    public static void WriteRequestHead(IBufferWriter<byte> output, WriteKindEnum kind, uint typeId, LatticeRevision? expectedRevision, LatticePosition? minimumPosition)
    {
        const int Most = 1 + sizeof(uint) + 1 + (2 * sizeof(ulong)) + 1 + (2 * sizeof(ulong));

        var span = output.GetSpan(Most);
        var at = 0;

        span[at++] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(span[at..], typeId);
        at += sizeof(uint);

        if (expectedRevision is { } revision)
        {
            span[at++] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(span[at..], revision.High);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(at + sizeof(ulong))..], revision.Low);
            at += 2 * sizeof(ulong);
        }
        else
        {
            span[at++] = 0;
        }

        at += WritePosition(span[at..], minimumPosition);
        output.Advance(at);
    }

    // The part of a request before its key; the reader stands at the key.
    public static ForwardedWrite ReadRequestHead(ref SequenceReader<byte> body)
    {
        if (!body.TryRead(out var kind) || kind > (byte)WriteKindEnum.Delete)
        {
            throw Protocol.Invalid("A forwarded write is empty or names a kind of write that is none.");
        }

        if (!body.TryReadLittleEndian(out int typeId))
        {
            throw Truncated("its entity type");
        }

        LatticeRevision? revision = null;

        if (ReadMark(ref body, "its expected revision"))
        {
            if (!body.TryReadLittleEndian(out long high) || !body.TryReadLittleEndian(out long low))
            {
                throw Truncated("its expected revision");
            }

            revision = new LatticeRevision((ulong)high, (ulong)low);

            // An insert has no condition but an absent key: the write path would take one with a revision
            // for a replace.
            if ((WriteKindEnum)kind == WriteKindEnum.Insert)
            {
                throw Protocol.Invalid("A forwarded insert carries an expected revision, which no insert has.");
            }
        }

        var position = ReadPosition(ref body, "its minimum position");

        return new ForwardedWrite((WriteKindEnum)kind, (uint)typeId, revision, position);
    }

    // The length of the root of an insert, a replace or an upsert, after its key.
    public static void WriteRootLength(IBufferWriter<byte> output, long rootLength)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(output.GetSpan(sizeof(ulong)), (ulong)rootLength);
        output.Advance(sizeof(ulong));
    }

    // The root of an insert, a replace or an upsert, after its key: what its length announces, which ends
    // the body exactly. Whether the root itself is valid is the check of a received root
    // (Load.BlockImport).
    public static ReadOnlySequence<byte> ReadRoot(ref SequenceReader<byte> body)
    {
        if (!body.TryReadLittleEndian(out long length))
        {
            throw Truncated("the length of its root");
        }

        if ((ulong)length != (ulong)body.Remaining)
        {
            throw Protocol.Invalid($"A forwarded write announces a root of {(ulong)length} bytes, and {body.Remaining} follow.");
        }

        var root = body.UnreadSequence;

        body.Advance(body.Remaining);

        return root;
    }

    // A Delete ends with its key.
    public static void ExpectEnd(in SequenceReader<byte> body)
    {
        if (!body.End)
        {
            throw Protocol.Invalid($"A forwarded delete goes on for {body.Remaining} bytes after its key.");
        }
    }

    // The bytes of the body of the reply of a write that succeeded.
    public const int ResultLength = 1 + (2 * sizeof(ulong)) + 1;

    // The reply of a write that succeeded as the payload of its one frame: its announced length and its
    // body, ResultLength bytes, written into the span, which has room for both.
    public static void WriteReply(Span<byte> payload, in LatticeWriteResult result)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(payload, ResultLength);

        var span = payload[sizeof(ulong)..];

        span[0] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(span[1..], result.Position.History);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(1 + sizeof(ulong))..], result.Position.Sequence);
        span[ResultLength - 1] = result.Changed ? (byte)1 : (byte)0;
    }

    // The reply of a write the master refused, with the identity of the unique index it names when it is
    // UniqueConstraintViolation. The message may be as long as the master made it, longer than a span of
    // the output holds, so it is encoded into the spans as the output gives them.
    public static void WriteReply(IBufferWriter<byte> output, LatticeException refusal, int? indexId)
    {
        var head = output.GetSpan(1 + sizeof(int) + sizeof(uint));
        var message = refusal.Message.AsSpan();

        head[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(head[1..], (int)refusal.Error);
        BinaryPrimitives.WriteUInt32LittleEndian(head[(1 + sizeof(int))..], (uint)Encoding.UTF8.GetByteCount(message));
        output.Advance(1 + sizeof(int) + sizeof(uint));

        while (!message.IsEmpty)
        {
            Utf8.FromUtf16(message, output.GetSpan(MostBytesOfACharacter), out var read, out var written);
            output.Advance(written);
            message = message[read..];
        }

        const int Most = 1 + sizeof(int) + 1 + sizeof(int) + 1 + (2 * sizeof(ulong)) + 1 + sizeof(int);

        var tail = output.GetSpan(Most);
        var at = WriteOptional(tail, refusal.Limit is { } limit ? (int)limit : null);

        at += WriteOptional(tail[at..], refusal.OperationIndex);
        at += WritePosition(tail[at..], refusal.CommittedPosition);
        at += WriteOptional(tail[at..], indexId);
        output.Advance(at);
    }

    // The reply in the body (see the class).
    public static ForwardedReply ReadReply(ReadOnlySequence<byte> body)
    {
        var reader = new SequenceReader<byte>(body);

        if (!ReadMark(ref reader, "a reply"))
        {
            var position = ReadBarePosition(ref reader, "its position");

            if (!reader.TryRead(out var changed) || changed > 1)
            {
                throw Protocol.Invalid("The reply of a forwarded write ends before Changed, or Changed is neither 0 nor 1.");
            }

            ExpectEndOfReply(reader);

            return new ForwardedReply(new LatticeWriteResult(position, changed == 1), null, null);
        }

        if (!reader.TryReadLittleEndian(out int error) || !Enum.IsDefined((LatticeErrorEnum)error))
        {
            throw Protocol.Invalid("The refusal of a forwarded write ends before its error, or names an error that is none.");
        }

        if (!reader.TryReadLittleEndian(out int length) || (uint)length > (ulong)reader.Remaining)
        {
            throw Protocol.Invalid("The refusal of a forwarded write does not hold the message it announces.");
        }

        string message;

        try
        {
            message = Protocol.StrictUtf8.GetString(reader.UnreadSequence.Slice(0, length));
        }
        catch (DecoderFallbackException exception)
        {
            throw Protocol.Invalid($"The message of the refusal of a forwarded write is not well-formed UTF-8 ({exception.Message}).");
        }

        reader.Advance(length);

        LatticeLimitEnum? limit = null;

        if (ReadMark(ref reader, "its limit"))
        {
            if (!reader.TryReadLittleEndian(out int value) || !Enum.IsDefined((LatticeLimitEnum)value))
            {
                throw Protocol.Invalid("The refusal of a forwarded write ends in its limit, or names a limit that is none.");
            }

            limit = (LatticeLimitEnum)value;
        }

        int? operationIndex = null;

        if (ReadMark(ref reader, "its operation index"))
        {
            if (!reader.TryReadLittleEndian(out int index))
            {
                throw Truncated("its operation index");
            }

            operationIndex = index;
        }

        var committed = ReadPosition(ref reader, "its committed position");
        int? indexId = null;

        if (ReadMark(ref reader, "its unique index"))
        {
            if (!reader.TryReadLittleEndian(out int id))
            {
                throw Truncated("its unique index");
            }

            indexId = id;
        }

        if ((indexId is not null) != ((LatticeErrorEnum)error == LatticeErrorEnum.UniqueConstraintViolation))
        {
            throw Protocol.Invalid($"The refusal of a forwarded write is {(LatticeErrorEnum)error} and {(indexId is null ? "names no" : "names a")} unique index; UniqueConstraintViolation names one and no other refusal does.");
        }

        ExpectEndOfReply(reader);

        return new ForwardedReply(null, new LatticeException((LatticeErrorEnum)error, message)
        {
            Limit = limit,
            OperationIndex = operationIndex,
            CommittedPosition = committed,
        }, indexId);
    }

    // The reply with the refusal naming its unique index by the name the entity type of the write gives
    // the identity in the reply (see the class); the reply as it is when it names none. ValidationFailed
    // when the entity type declares no unique index of that identity: no master sends one.
    public static ForwardedReply NameIndex(in ForwardedReply reply, LatticeEntityType entityType)
    {
        if (reply.IndexId is not { } id)
        {
            return reply;
        }

        foreach (var index in entityType.Indexes)
        {
            if (index.Id == id && index.IsUnique)
            {
                var refusal = reply.Refusal!;

                return reply with
                {
                    Refusal = new LatticeException(refusal.Error, refusal.Message)
                    {
                        Limit = refusal.Limit,
                        OperationIndex = refusal.OperationIndex,
                        CommittedPosition = refusal.CommittedPosition,
                        IndexName = index.Name,
                    },
                };
            }
        }

        throw Protocol.Invalid($"The master refused a forwarded write of {entityType.Name} for the unique index {id}, which {entityType.Name} does not declare.");
    }

    private static LatticeException Truncated(string part) => Protocol.Invalid($"A forwarded write or its reply ends before {part}.");

    private static void ExpectEndOfReply(in SequenceReader<byte> reader)
    {
        if (!reader.End)
        {
            throw Protocol.Invalid($"The reply of a forwarded write goes on for {reader.Remaining} bytes after its end.");
        }
    }

    // A mark of presence: true for 1, false for 0, ValidationFailed for anything else or none.
    private static bool ReadMark(ref SequenceReader<byte> reader, string part)
    {
        if (!reader.TryRead(out var mark) || mark > 1)
        {
            throw Protocol.Invalid($"A forwarded write or its reply ends before {part}, or marks it with a value that is neither 0 nor 1.");
        }

        return mark == 1;
    }

    // An optional Int32: its mark and the value; the bytes written.
    private static int WriteOptional(Span<byte> span, int? value)
    {
        if (value is not { } present)
        {
            span[0] = 0;

            return 1;
        }

        span[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[1..], present);

        return 1 + sizeof(int);
    }

    // An optional position: its mark, its history and its sequence; the bytes written.
    private static int WritePosition(Span<byte> span, LatticePosition? position)
    {
        if (position is not { } present)
        {
            span[0] = 0;

            return 1;
        }

        span[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(span[1..], present.History);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(1 + sizeof(ulong))..], present.Sequence);

        return 1 + (2 * sizeof(ulong));
    }

    // An optional position: its mark, then the position.
    private static LatticePosition? ReadPosition(ref SequenceReader<byte> reader, string part) =>
        ReadMark(ref reader, part) ? ReadBarePosition(ref reader, part) : null;

    // A position without its mark; one whose history is 0 is no position.
    private static LatticePosition ReadBarePosition(ref SequenceReader<byte> reader, string part)
    {
        if (!reader.TryReadLittleEndian(out long history) || !reader.TryReadLittleEndian(out long sequence))
        {
            throw Truncated(part);
        }

        if (history == 0)
        {
            throw Protocol.Invalid($"A forwarded write or its reply gives {part} with the history 0, which is no position.");
        }

        return new LatticePosition((ulong)history, (ulong)sequence);
    }
}
