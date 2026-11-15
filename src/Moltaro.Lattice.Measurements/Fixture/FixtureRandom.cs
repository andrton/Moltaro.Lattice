namespace Moltaro.Lattice.Measurements.Fixture;

// The generator of the reference fixture: SplitMix64, a generator of 64-bit values whose sequence
// for a seed is fixed by its few lines of arithmetic, so that a seed gives the same values in every
// version of .NET and on every machine, which System.Random does not promise. It is not meant for
// anything but synthetic data.
internal sealed class FixtureRandom(ulong seed)
{
    private ulong _state = seed;

    // The next value of the sequence.
    public ulong Next()
    {
        var value = _state += 0x9E37_79B9_7F4A_7C15UL;

        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;

        return value ^ (value >> 31);
    }

    // A value from 0 to bound - 1: the high half of the next value times the bound, so that every
    // value of the range is about equally likely and the same on every run.
    public int Below(int bound) => (int)Math.BigMul(Next(), (ulong)bound, out _);
}
