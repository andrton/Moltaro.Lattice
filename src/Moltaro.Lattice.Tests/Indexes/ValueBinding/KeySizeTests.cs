using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A key and a bound are small values (IndexKey), and a value that keeps the type of a null makes neither
// larger: the type of a null lies in the padding of the value. The sizes are those of a 64-bit process: a
// value of a key is 32 bytes, its bits, a reference, its type and the type of a null; a bound one value
// and whether it is inclusive; a key four values, two optional bounds, their count and whether a factory
// made it.
public sealed class KeySizeTests
{
    [Fact]
    public void Index_key_and_bound_keep_the_size_of_a_value_with_a_null_that_has_a_type()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(40, Unsafe.SizeOf<IndexBound>());
        Assert.Equal(232, Unsafe.SizeOf<IndexKey>());
    }
}
