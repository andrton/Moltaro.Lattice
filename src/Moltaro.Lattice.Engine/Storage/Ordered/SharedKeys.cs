using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Storage.Ordered;

// The shared keys of one entry of an ordered index, one at a time: the String parts that reference a
// text entry instead of holding a copy of their text (IndexLayout.TextCopyLimit), in the order of the
// parts of the layout, each given as a root names its text entry, the descriptor of kind Text with the
// length and the address of the entry. The walks over the entries of a node (NodeStorage) and the
// publication of the texts of an entry an insert adds (Engine.Indexes.OrderedTree) take them from here;
// docs/design/ordered-index.md, "Text references", says what the references of a node are. An entry of
// an index whose parts are all of fixed width has no shared key, and the walk ends at its first step.
//
// A ref struct over the entry, for foreach, as RootDescriptors walks a root: it allocates nothing, calls
// nothing virtual and reads the entry only, so it is valid while the node that holds the entry is.
// NodeStorage.Retire says why it is a ref struct and not a span filled by the caller.
internal ref struct SharedKeys
{
    // The parts of the layout, none for a layout without a String part.
    private readonly ReadOnlySpan<EntryPart> _parts;

    private readonly ReadOnlySpan<byte> _entry;

    // The part the next step looks at, and where it begins in the entry.
    private int _place;

    private int _offset;

    private ValueDescriptor _current;

    // The shared keys of the entry, which is laid out by the layout, before the first.
    public SharedKeys(IndexLayout layout, ReadOnlySpan<byte> entry)
    {
        _parts = layout.HasText ? layout.Parts : default;
        _entry = entry;
        _place = 0;
        _offset = IndexLayout.IdentityBytes + layout.NullBitmapBytes;
        _current = default;
    }

    // The descriptor of the shared key the last step found.
    public readonly ValueDescriptor Current => _current;

    public readonly SharedKeys GetEnumerator() => this;

    // Steps over the parts to the next shared key; false after the last part.
    public bool MoveNext()
    {
        while (_place < _parts.Length)
        {
            var descriptor = ReadPart(_parts[_place++], _entry, ref _offset);
            if (descriptor.Kind == DescriptorKindEnum.Text)
            {
                _current = descriptor;
                return true;
            }
        }

        return false;
    }

    // Reads one part, including an unshared or fixed-width part, so a reclamation
    // turn can count it even when it does not hold a text reference.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueDescriptor ReadPart(in EntryPart part, ReadOnlySpan<byte> entry, ref int offset)
    {
        if (!part.IsText)
        {
            offset += part.Width;
            return default;
        }

        var at = offset;
        var head = entry[at];
        offset += IndexLayout.TextPartBytes(head);
        return head == IndexLayout.SharedTextHead ? IndexLayout.SharedDescriptorOf(entry[at..]) : default;
    }
}
