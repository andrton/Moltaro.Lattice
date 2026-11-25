using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Values;

// A descriptor in a prepared or published native block. The arena aligns the block,
// but a packed record can put a 16-byte slot at any byte offset. Round the usual
// location at slot start plus eight down to an 8-byte boundary inside the slot. Where this displaces
// the metadata, pack kind and length in the four bytes on the longer side of the word.
// The existing value lengths, record counts and lookup counts fit in 28 bits.
//
// The metadata remains fixed after preparation. The publishing thread can replace the
// aligned location with a release store. An acquire load obtains one complete location
// before the reader follows it. The read epoch keeps the previous target alive.
// Canonical scratch and transfer bytes use ValueBlocks. See milestone-1-engine-contract.md,
// "Collections and strings" and "Reader protection, publication and compaction".
internal static class ResidentDescriptor
{
    private const int KindBits = 4;

    private const uint KindMask = (1U << KindBits) - 1;

    // Reads a native slot under the writes lock or a read epoch, acquiring its location.
    public static ValueDescriptor Read(ReadOnlySpan<byte> place)
    {
        Debug.Assert(place.Length >= ValueBlocks.DescriptorSize);
        ref var first = ref MemoryMarshal.GetReference(place);
        var locationAt = LocationAt(ref first);
        var location = Volatile.Read(ref Location(ref first, locationAt));

        if (locationAt == sizeof(ulong))
        {
            return new ValueDescriptor
            {
                Kind = Unsafe.ReadUnaligned<DescriptorKindEnum>(ref first),
                Length = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref first, sizeof(uint))),
                Location = location,
            };
        }

        var metadata = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref first, MetadataAt(locationAt)));
        return new ValueDescriptor
        {
            Kind = (DescriptorKindEnum)(metadata & KindMask),
            Length = metadata >> KindBits,
            Location = location,
        };
    }

    // Encodes a native slot that preparation has not made reachable.
    public static void Write(Span<byte> place, in ValueDescriptor descriptor)
    {
        Debug.Assert(place.Length >= ValueBlocks.DescriptorSize);
        Debug.Assert((uint)descriptor.Kind <= KindMask && descriptor.Length <= uint.MaxValue >> KindBits);
        ref var first = ref MemoryMarshal.GetReference(place);
        var locationAt = LocationAt(ref first);

        if (locationAt == sizeof(ulong))
        {
            ValueBlocks.Write(place, descriptor);
            return;
        }

        place[..ValueBlocks.DescriptorSize].Clear();
        var metadata = (descriptor.Length << KindBits) | (uint)descriptor.Kind;
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref first, MetadataAt(locationAt)), metadata);
        Location(ref first, locationAt) = descriptor.Location;
    }

    // Releases a new location under the writes lock, once the destination is ready.
    public static void PublishLocation(Span<byte> place, ArenaAddress destination)
    {
        Debug.Assert(place.Length >= ValueBlocks.DescriptorSize);
        ref var first = ref MemoryMarshal.GetReference(place);
        Volatile.Write(ref Location(ref first, LocationAt(ref first)), destination.Value);
    }

    // Reads immutable metadata, using the original block offset even for a copy.
    public static uint ReadLength(ReadOnlySpan<byte> place, int blockOffset)
    {
        Debug.Assert(place.Length >= ValueBlocks.DescriptorSize);
        ref var first = ref MemoryMarshal.GetReference(place);
        var locationAt = LocationAt((nuint)blockOffset);

        if (locationAt == sizeof(ulong))
        {
            return Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref first, sizeof(uint)));
        }

        return Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref first, MetadataAt(locationAt))) >> KindBits;
    }

    private static ref ulong Location(ref byte first, int at) =>
        ref Unsafe.As<byte, ulong>(ref Unsafe.Add(ref first, at));

    private static unsafe int LocationAt(ref byte first) => LocationAt((nuint)Unsafe.AsPointer(ref first));

    private static int LocationAt(nuint offset) => sizeof(ulong) - (int)(offset & (sizeof(ulong) - 1));

    private static int MetadataAt(int locationAt) =>
        locationAt < sizeof(uint) ? locationAt + sizeof(ulong) : locationAt - sizeof(uint);
}
