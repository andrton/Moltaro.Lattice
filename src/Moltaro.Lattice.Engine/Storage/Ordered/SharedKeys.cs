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
            ref readonly var part = ref _parts[_place++];

            if (!part.IsText)
            {
                _offset += part.Width;

                continue;
            }

            var at = _offset;
            var head = _entry[at];

            _offset += IndexLayout.TextPartBytes(head);

            if (head == IndexLayout.SharedTextHead)
            {
                _current = IndexLayout.SharedDescriptorOf(_entry[at..]);

                return true;
            }
        }

        return false;
    }
}
