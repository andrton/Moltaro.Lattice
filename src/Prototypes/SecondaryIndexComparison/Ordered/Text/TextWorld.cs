using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// Synthetic entities with one indexed text field, laid out as the engine lays out a root, so that a
// comparison that reads the key from the root, or from a shared text entry, pays the loads it would
// pay in the engine:
// - a control entry of 48 bytes per entity in a managed array, as RootEntry in the pages of the root
//   table, whose first word is the address of the root block, read with acquire semantics;
// - a root block in the arena: an 8-byte Id field, one descriptor of 16 bytes (kind, length,
//   location: Storage/ValueDescriptor.cs), and a Plain text of at most 64 bytes inline after them
//   (ValueBlocks.InlineLimit), a longer one in a value block of its own;
// - for a shared text (form 3), the text in an entry of its own, one block whose payload is the text
//   bytes (Storage/Text/TextTable.cs), which the root's descriptor names by kind Text. The managed slot
//   of 32 bytes and the bucket word the engine's text table keeps per entry are not modelled; they are
//   added by arithmetic where bytes are reported.
//
// Text is ASCII here, so the order of its bytes is the ordinal order of UTF-16 code units, which the
// engine's ValueComparer gives; the engine decodes stored UTF-8 into pieces of UTF-16 on the stack
// before it compares, which costs every form that compares full text more than it costs here.
internal sealed unsafe class TextWorld
{
    public const int InlineLimit = 64;

    private const uint InlineKind = 1;

    private const uint BlockKind = 2;

    private const uint TextKind = 4;

    // The Id field, then the descriptor.
    private const int DescriptorOffset = 8;

    private const int FixedBytes = DescriptorOffset + 16;

    private readonly NodeArena _arena;

    private readonly ControlEntry[] _entries;

    public TextWorld(NodeArena arena, int capacity)
    {
        _arena = arena;
        _entries = new ControlEntry[capacity + 1];
    }

    // The arena slots of the roots, of the value blocks of long Plain text, and of the text entries.
    public long RootBytes { get; private set; }

    public long ValueBytes { get; private set; }

    public long TextEntryBytes { get; private set; }

    // The first eight bytes of a text, big-endian, zero after its end: a prefix whose order as an
    // unsigned number never contradicts the order of the texts.
    public static ulong PrefixOf(ReadOnlySpan<byte> text)
    {
        Span<byte> first = stackalloc byte[8];

        first.Clear();
        text[..Math.Min(8, text.Length)].CopyTo(first);

        return BinaryPrimitives.ReadUInt64BigEndian(first);
    }

    public static int CompareText(byte* left, int leftLength, byte* right, int rightLength) =>
        new ReadOnlySpan<byte>(left, leftLength).SequenceCompareTo(new ReadOnlySpan<byte>(right, rightLength));

    // Stores the root of entity id with its text, the text in an entry of its own when shared; returns
    // the address of that entry, or 0.
    public ulong Add(ulong id, ReadOnlySpan<byte> text, bool shared)
    {
        var inline = !shared && text.Length <= InlineLimit;
        var root = _arena.Allocate(FixedBytes + (inline ? text.Length : 0), prepared: false);
        var payload = _arena.Payload(root);
        var textAddress = 0UL;

        *(ulong*)payload = id;

        if (inline)
        {
            Describe(payload, InlineKind, text.Length, FixedBytes);
            text.CopyTo(new Span<byte>(payload + FixedBytes, text.Length));
        }
        else
        {
            textAddress = _arena.Allocate(text.Length, prepared: false);
            text.CopyTo(new Span<byte>(_arena.Payload(textAddress), text.Length));
            Describe(payload, shared ? TextKind : BlockKind, text.Length, textAddress);

            if (shared)
            {
                TextEntryBytes += _arena.SlotOf(textAddress);
            }
            else
            {
                ValueBytes += _arena.SlotOf(textAddress);
                textAddress = 0;
            }
        }

        RootBytes += _arena.SlotOf(root);
        Volatile.Write(ref _entries[id].Root, root);

        return textAddress;
    }

    // The text the root of entity id stores, inside the protection of reads: control entry, root
    // block, descriptor, then the text inline or in its block.
    public byte* TextOf(ulong id, out int length)
    {
        var payload = _arena.Payload(Volatile.Read(ref _entries[id].Root));
        var descriptor = payload + DescriptorOffset;

        length = *(int*)(descriptor + 4);

        var location = *(ulong*)(descriptor + 8);

        return *(uint*)descriptor == InlineKind ? payload + location : _arena.Payload(location);
    }

    // The text of a shared entry: its payload, of the length it was allocated with.
    public byte* SharedText(ulong address, out int length)
    {
        length = _arena.PayloadLength(address);

        return _arena.Payload(address);
    }

    // What a read by key copies of the text: from the root block, or from the entry it names.
    public int CopyText(ulong id, Span<byte> into)
    {
        var text = TextOf(id, out var length);

        new ReadOnlySpan<byte>(text, length).CopyTo(into);

        return length;
    }

    private static void Describe(byte* payload, uint kind, int length, ulong location)
    {
        var descriptor = payload + DescriptorOffset;

        *(uint*)descriptor = kind;
        *(int*)(descriptor + 4) = length;
        *(ulong*)(descriptor + 8) = location;
    }

    // The 48 bytes of a control entry; the address of the root first.
    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private struct ControlEntry
    {
        public ulong Root;
    }
}
