using Moltaro.Lattice.Storage.Text;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Ordered;

// A turn on an epoch-safe retired node releases at most one key reference or one
// native slot. The live tree already owns its references, including separators.
internal struct NodeReclamation
{
    private int _entry;

    private int _part;

    private int _offset;

    private TextReclamation _text;

    private bool _hasText;

    public bool Step(Arena arena, IndexLayout layout, ArenaAddress address)
    {
        if (_hasText)
        {
            _hasText = !arena.Texts.ReleaseStep(ref _text);
            return false;
        }

        var node = new IndexNode(layout, arena.Payload(address));
        if (!layout.HasText || _entry == node.Count)
        {
            arena.Free(address);
            return true;
        }

        if (_part == 0)
        {
            _offset = IndexLayout.IdentityBytes + layout.NullBitmapBytes;
        }

        if (_part == layout.Parts.Length)
        {
            _entry++;
            _part = 0;
            return false;
        }

        var descriptor = SharedKeys.ReadPart(layout.Parts[_part++], node.EntryAt(_entry), ref _offset);
        if (descriptor.Kind == DescriptorKindEnum.Text)
        {
            _text = new TextReclamation(ValueBlocks.AddressOf(descriptor));
            _hasText = true;
        }

        return false;
    }
}
