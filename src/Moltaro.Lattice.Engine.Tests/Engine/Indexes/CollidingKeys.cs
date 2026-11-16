namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Int64 keys that share one hash of the primary index when its seed is 0, built as an outsider who
// knows the function would build them (review round 2 of Epic 1, sprint S08, part 1; Q50 of the open
// questions): the index hashes an Int64 key by the 64-bit finalizer of MurmurHash3 and keeps the low
// 32 bits (Entities.EntityTable), and the finalizer can be inverted, so keys whose finalized values
// differ only in their high 32 bits all fall on one hash.
//
// The helper is tied to that function and to how the seed enters it. That is acceptable here because
// the function is the subject of the test: the planner's answer to Q50 keeps the finalizer and mixes
// the seed into the key before it, so with the seed 0, under which the key enters unchanged, these keys
// collide exactly as they do without a seed, and under any other seed the finalizer scatters them.
// Mix repeats the finalizer, so that a test can check the keys against it, apart from the engine. If
// the implementer of S08, part 2 makes the seed enter otherwise, the keys no longer collide under the
// seed 0; that is for the implementer to report, not to edit here.
internal static class CollidingKeys
{
    private const ulong First = 0xFF51AFD7ED558CCD;

    private const ulong Second = 0xC4CEB9FE1A85EC53;

    // The low 32 bits every key is built to share.
    public const uint Hash = 0x9E3779B9;

    // The given number of distinct keys whose finalized values have the low 32 bits Hash.
    public static long[] Of(int count) =>
        [.. Enumerable.Range(1, count).Select(index => unchecked((long)Unmix(((ulong)index << 32) | Hash)))];

    // The 64-bit finalizer fmix64 of MurmurHash3, by Austin Appleby, in the public domain.
    public static ulong Mix(ulong value)
    {
        value ^= value >> 33;
        value *= First;
        value ^= value >> 33;
        value *= Second;
        value ^= value >> 33;

        return value;
    }

    // The inverse of Mix: a shift by 33 bits of a 64-bit value, exclusive-ored with the value, is its
    // own inverse, and a multiplication by an odd constant is undone by its inverse modulo 2^64.
    private static ulong Unmix(ulong value)
    {
        value ^= value >> 33;
        value *= InverseOf(Second);
        value ^= value >> 33;
        value *= InverseOf(First);
        value ^= value >> 33;

        return value;
    }

    // The inverse of an odd number modulo 2^64, by Newton's iteration, which doubles the correct low
    // bits at each step from the three that the number itself has.
    private static ulong InverseOf(ulong odd)
    {
        var inverse = odd;

        for (var step = 0; step < 5; step++)
        {
            inverse *= 2 - (odd * inverse);
        }

        return inverse;
    }
}
