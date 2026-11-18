using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Hashing;

// Adapted from the .NET runtime (https://github.com/dotnet/runtime), the class Marvin of
// src/libraries/System.Private.CoreLib/src/System/Marvin.cs at commit
// 2dada4c5d6fb82f9d7f274d7bd980e508cdf52e2: Marvin32, the seeded hash of text that string.GetHashCode
// computes; MIT licence, Copyright (c) .NET Foundation and Contributors (see THIRD-PARTY-NOTICES.md).
//
// The seeded hash of the engine: of the text of a key in the primary index
// (Engine.Comparison.ValueComparer.TextHash), over its UTF-16 code units, and of the stored bytes of a
// text entry in the text table (Storage.Text.TextTable), over the bytes of the database encoding. A seed
// of 64 bits, the seed of the instance, is its state p0 and p1; every block of four bytes, read in the
// order of the machine, is added to p0 and mixed by the block function; the bytes left at the end, with
// the byte 0x80 after them and zeros, make the last block; two rounds more, and the hash is p1
// exclusive-or p0. What is taken from the source: the state, the block function with its four
// rotations, the main loop over eight bytes with two blocks, and the last block with its 0x80. What
// differs, and why:
// - The bytes come in pieces: the pieces a comparison without case maps a text into
//   (Schema.TextComparison), and the pieces in which the text table encodes or receives the bytes of a
//   text, so the state is a value that Append takes piece by piece and Finish ends; the source takes the
//   whole text in one call. A piece leaves at most three bytes over, which wait for the first bytes of
//   the next piece to make a block; the source reads the last bytes by one read that overlaps those it
//   has read. Blocks are made of the bytes in their order whatever the pieces, so a text has one hash
//   in one piece or in many, and for text of UTF-16 code units the hash is the source's.
// - The hash is unsigned, as the bucket table takes it; the source returns it as an int.
internal struct Marvin
{
    private uint _p0;

    private uint _p1;

    // The bytes left over by the last piece, at most three, in the order they came, the first in the
    // lowest byte, as a read of four bytes on a little-endian machine gives them; and how many.
    private uint _waiting;

    private int _waitingCount;

    public Marvin(ulong seed)
    {
        _p0 = (uint)seed;
        _p1 = (uint)(seed >> 32);
    }

    // The hash of the text under the seed. Allocates nothing.
    public static uint Hash(ReadOnlySpan<char> text, ulong seed)
    {
        var marvin = new Marvin(seed);

        marvin.Append(text);

        return marvin.Finish();
    }

    // Adds the code units of the text after what was added before.
    public void Append(ReadOnlySpan<char> text) => Append(MemoryMarshal.AsBytes(text));

    // Adds the bytes after what was added before.
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var p0 = _p0;
        var p1 = _p1;

        // The bytes left over complete a block with the first bytes of this piece.
        while (_waitingCount > 0 && !bytes.IsEmpty)
        {
            _waiting |= (uint)bytes[0] << (8 * _waitingCount);
            _waitingCount++;
            bytes = bytes[1..];

            if (_waitingCount == sizeof(uint))
            {
                p0 += InMachineOrder(_waiting);
                Block(ref p0, ref p1);
                _waiting = 0;
                _waitingCount = 0;
            }
        }

        ref var data = ref MemoryMarshal.GetReference(bytes);
        var count = (uint)bytes.Length;

        for (var loop = count / 8; loop > 0; loop--)
        {
            p0 += Unsafe.ReadUnaligned<uint>(ref data);
            var next = Unsafe.ReadUnaligned<uint>(ref Unsafe.AddByteOffset(ref data, 4));

            Block(ref p0, ref p1);
            p0 += next;
            Block(ref p0, ref p1);

            data = ref Unsafe.AddByteOffset(ref data, 8);
        }

        if ((count & 0b_0100) != 0)
        {
            p0 += Unsafe.ReadUnaligned<uint>(ref data);
            Block(ref p0, ref p1);
            data = ref Unsafe.AddByteOffset(ref data, 4);
        }

        for (var left = 0; left < (int)(count & 0b_0011); left++)
        {
            _waiting |= (uint)Unsafe.AddByteOffset(ref data, left) << (8 * _waitingCount);
            _waitingCount++;
        }

        _p0 = p0;
        _p1 = p1;
    }

    // The hash of everything added: the last block, the bytes left over or none, then 0x80, and the
    // two final rounds.
    public readonly uint Finish()
    {
        var p0 = _p0;
        var p1 = _p1;

        p0 += InMachineOrder(_waiting | (0x80u << (8 * _waitingCount)));

        Block(ref p0, ref p1);
        Block(ref p0, ref p1);

        return p1 ^ p0;
    }

    // Four bytes gathered with the first in the lowest byte, as a read of those four bytes in the order of
    // the machine gives them.
    private static uint InMachineOrder(uint littleEndian) =>
        BitConverter.IsLittleEndian ? littleEndian : BinaryPrimitives.ReverseEndianness(littleEndian);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Block(ref uint rp0, ref uint rp1)
    {
        var p0 = rp0;
        var p1 = rp1;

        p1 ^= p0;
        p0 = BitOperations.RotateLeft(p0, 20);

        p0 += p1;
        p1 = BitOperations.RotateLeft(p1, 9);

        p1 ^= p0;
        p0 = BitOperations.RotateLeft(p0, 27);

        p0 += p1;
        p1 = BitOperations.RotateLeft(p1, 19);

        rp0 = p0;
        rp1 = p1;
    }
}
