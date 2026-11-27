namespace Moltaro.Lattice.Storage.Text;

// An allocation group measured from resident managed objects under Writes.
internal readonly record struct TextAllocationShape(int InstanceCount, int ObjectFieldBytes, int ArrayElementBytes, int ArrayLength)
{
    // The bytes of an object whose instance fields take fieldBytes, on a 64-bit runtime: the object
    // header and the method table, a word each, and the fields, rounded up to a word. Each class of the
    // text table states its fields from its declarations, for its Bytes and its shape.
    public static long ObjectBytes(int fieldBytes) => (2L * IntPtr.Size + fieldBytes + IntPtr.Size - 1) & ~(IntPtr.Size - 1L);
}
