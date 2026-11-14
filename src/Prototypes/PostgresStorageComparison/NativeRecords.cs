using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed unsafe class NativeRecords : IDisposable
{
    public const int RecordCount = 65536;
    private readonly object[] _gates;
    private long* _values;

    public NativeRecords(int count = RecordCount)
    {
        Count = count;
        _gates = new object[count];
        _values = (long*)NativeMemory.AllocZeroed((nuint)(count * 16));
        for (var key = 0; key < count; key++)
        {
            _gates[key] = new object();
            _values[key * 2 + 1] = ~0L;
        }
    }

    public int Count { get; }

    // Each gate protects the two fields of one root. No gate spans storage I/O.
    // The owner joins all worker threads before disposal; views never escape.
    public void Apply(Mutation mutation)
        => Apply(mutation.Key, mutation.Sequence);

    public void Apply(int key, long value)
    {
        lock (_gates[key])
        {
            _values[key * 2] = value;
            _values[key * 2 + 1] = ~value;
        }
    }

    public long Read(int key)
    {
        lock (_gates[key])
        {
            var value = _values[key * 2];
            if (_values[key * 2 + 1] != ~value)
            {
                throw new InvalidDataException("Torn root read.");
            }

            return value;
        }
    }

    public void Dispose()
    {
        NativeMemory.Free(_values);
        _values = null;
    }
}
