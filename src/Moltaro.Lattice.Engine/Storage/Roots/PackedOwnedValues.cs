using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The packed elements of a List of an owned value whose fields hold no descriptor (Values.PackedElements): a
// record holds List<LatticeOwnedValue?>, and the record of an element is the structure of the owned value,
// its bitmap and its fixed-width fields, written and read as the fields of an owned value in a root are
// (NestedValues), with nothing out of line.
internal sealed class PackedOwnedValues : PackedElements
{
    // The record of the element.
    private readonly StructureLayout _element;

    public PackedOwnedValues(StructureLayout element)
        : base(element.Size)
    {
        _element = element;
    }

    public override int Count(object list) => ((IList)list).Count;

    public override void Write(object list, int first, Span<byte> records)
    {
        var elements = (List<LatticeOwnedValue?>)list;

        for (var index = 0; index < records.Length / Stride; index++)
        {
            // A record holds no descriptor, so its holder takes nothing of an arena.
            var holder = new ValueHolder(null!, default, 0, default, records.Slice(index * Stride, Stride), origin: 0, address: default, inlineEnd: Stride);

            NestedValues.StartRecord(ref holder, _element, at: 0);

            if (elements[first + index] is { } element)
            {
                NestedValues.Write(ref holder, _element, at: 0, position: 0, element);
            }
        }
    }

    public override object Create(int count) => new List<LatticeOwnedValue?>(count);

    public override void Read(object list, ReadOnlySpan<byte> records)
    {
        var elements = (List<LatticeOwnedValue?>)list;

        for (var offset = 0; offset < records.Length; offset += Stride)
        {
            elements.Add((LatticeOwnedValue?)NestedValues.Read(null!, _element, records.Slice(offset, Stride), origin: 0, at: 0, position: 0, encoding: default));
        }
    }
}
