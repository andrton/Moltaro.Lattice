using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Engine.Protection;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet), the struct Entry of
// libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.cs at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation (see
// THIRD-PARTY-NOTICES.md).
//
// One slot of the table of ReadEpochs: the epoch a read announced when it entered, or 0 while no
// read holds the slot. A slot takes 128 bytes, so that two slots never share a cache line, nor the
// pair of lines that the processor fetches together: a read writes only its own slot, and reads on
// other processors do not slow it down. LightEpoch pads its entries to 64 bytes and keeps a thread
// id and words of its users beside the epoch; a read here needs neither.
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct EpochSlot
{
    [FieldOffset(0)]
    public long Epoch;
}
