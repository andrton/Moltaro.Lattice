using System.Buffers;

namespace Moltaro.Lattice.Access.Values;

// Whether text is well-formed UTF-16: every high surrogate is followed by a low one, and every low
// surrogate follows a high one. Text that is not, a surrogate without its pair, is never stored and
// never replaced by another character (Q04.1, Q04.5): the validation of a write refuses it, and so
// does the check of a String key given as an argument (the planner's answer to Q41). The engine's root
// writer asks the same, as the guard of the form it stores.
internal static class WellFormedText
{
    // Every surrogate, high and low, U+D800 to U+DFFF, for a vectorized search. It is immutable and
    // made once, so it is shared by every caller. It stands in for IndexOfAnyInRange, which until the
    // JIT has optimized it boxes its bounds on every call, so that a check would not be free of managed
    // allocation from its first calls on.
    private static readonly SearchValues<char> Surrogates =
        SearchValues.Create([.. Enumerable.Range(0xD800, 0x800).Select(code => (char)code)]);

    // One pass over the text, vectorized while it meets no surrogate, which is the common case; it
    // allocates nothing.
    public static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        var index = text.IndexOfAny(Surrogates);

        while (index >= 0)
        {
            if (!char.IsHighSurrogate(text[index]) || index + 1 == text.Length || !char.IsLowSurrogate(text[index + 1]))
            {
                return false;
            }

            index += 2;

            var next = text[index..].IndexOfAny(Surrogates);

            index = next < 0 ? -1 : index + next;
        }

        return true;
    }
}
