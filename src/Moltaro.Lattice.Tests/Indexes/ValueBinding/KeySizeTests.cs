using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A key and a bound keep their size when a value keeps the type of a null (IndexKey: a key is a small
// value; the brief of S15, part 2: the value of a key keeps the type of such a null without growing). The
// sizes are those of a 64-bit process before the binding rules: a value of a key is 32 bytes, its bits, a
// reference and its type; a bound one value and whether it is inclusive; a key four values, two optional
// bounds, their count and whether a factory made it.
public sealed class KeySizeTests
{
    [Fact]
    public void Key_and_bound_have_the_size_they_had_before_a_null_kept_its_type()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, Unsafe.SizeOf<IndexBound>());
        Assert.Equal(232, Unsafe.SizeOf<IndexKey>());
    }
}
