using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Engine.Comparison;

// Adapted from the .NET runtime (https://github.com/dotnet/runtime), the class Marvin of
// src/libraries/System.Private.CoreLib/src/System/Marvin.cs at commit
// 2dada4c5d6fb82f9d7f274d7bd980e508cdf52e2: Marvin32, the seeded hash of text that string.GetHashCode
// computes; MIT licence, Copyright (c) .NET Foundation and Contributors (see THIRD-PARTY-NOTICES.md).
//
// The hash of text in the primary index (ValueComparer.TextHash): a seed of 64 bits, the seed of the
// instance, is its state p0 and p1; every block of four bytes of the text, read in the order of the
// machine, is added to p0 and mixed by the block function; the bytes left at the end, with the byte
// 0x80 after them and zeros, make the last block; two rounds more, and the hash is p1 exclusive-or p0.
// What is taken from the source: the state, the block function with its four rotations, the main loop
// over eight bytes with two blocks, and the last block with its 0x80. What differs, and why:
// - The text comes in pieces, the pieces a comparison without case maps it into (TextComparison), so
//   the state is a value that Append takes piece by piece and Finish ends; the source takes the whole
//   text in one call. Text is UTF-16, so a piece leaves at most one code unit over, two bytes, which
//   waits for the first code unit of the next piece to make a block; the source reads the last bytes
//   by one read that overlaps those it has read. For the same text the hash is the source's, in one
//   piece or in many: Hash and Append compute the blocks of the bytes of the text in their order.
// - The hash is unsigned, as the index takes it; the source returns it as an int.
internal struct Marvin
{
    private uint _p0;

    private uint _p1;

    // A code unit left over by the last piece, which waits for the next one to make a block.
    private uint _waiting;

    private bool _isWaiting;

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

    // Adds the code units of the text after those added before.
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        var p0 = _p0;
        var p1 = _p1;

        if (_isWaiting)
        {
            // The two code units in the order of the machine, as a read of four bytes gives them.
            p0 += BitConverter.IsLittleEndian ? _waiting | ((uint)text[0] << 16) : (_waiting << 16) | text[0];
            Block(ref p0, ref p1);
            text = text[1..];
            _isWaiting = false;
        }

        ref var data = ref Unsafe.As<char, byte>(ref MemoryMarshal.GetReference(text));
        var count = (uint)text.Length * sizeof(char);

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

        if ((count & 0b_0010) != 0)
        {
            _waiting = Unsafe.ReadUnaligned<ushort>(ref data);
            _isWaiting = true;
        }

        _p0 = p0;
        _p1 = p1;
    }

    // The hash of everything added: the last block, the two bytes of a code unit left over or none,
    // then 0x80, and the two final rounds.
    public readonly uint Finish()
    {
        var p0 = _p0;
        var p1 = _p1;

        if (BitConverter.IsLittleEndian)
        {
            p0 += _isWaiting ? 0x0080_0000u | _waiting : 0x80u;
        }
        else
        {
            p0 += _isWaiting ? (_waiting << 16) | 0x8000u : 0x8000_0000u;
        }

        Block(ref p0, ref p1);
        Block(ref p0, ref p1);

        return p1 ^ p0;
    }

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
