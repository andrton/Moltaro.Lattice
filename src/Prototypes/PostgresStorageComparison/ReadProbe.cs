using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class ReadProbe : IDisposable
{
    public const int SampleEvery = 4096;
    private readonly Thread _thread;
    private readonly NativeRecords _records;
    private readonly bool _pinReader;
    private readonly int _sampleEvery;
    private readonly long[] _samples = new long[500000];
    private int _sampleCount;
    private long _reads;
    private long _missedSamples;
    private long _checksum;
    private long _started;
    private long _ended;
    private volatile bool _stop;
    private Exception? _failure;

    public ReadProbe(NativeRecords records, bool pinReader = false, int sampleEvery = SampleEvery)
    {
        if (sampleEvery <= 0 || (sampleEvery & (sampleEvery - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleEvery), "Sampling interval must be a power of two.");
        }

        _sampleEvery = sampleEvery;
        _records = records;
        _pinReader = pinReader;
        _thread = new Thread(Read) { IsBackground = true, Name = "Lattice native reader" };
        _thread.Start();
    }

    private void Read()
    {
        uint random = 20261112;
        try
        {
            using var affinity = _pinReader ? new ReaderThreadAffinity() : null;
            _started = Stopwatch.GetTimestamp();
            while (!_stop)
            {
                random ^= random << 13;
                random ^= random >> 17;
                random ^= random << 5;
                var key = (int)(random % _records.Count);
                if ((_reads & (_sampleEvery - 1)) == 0)
                {
                    var started = Stopwatch.GetTimestamp();
                    _checksum ^= _records.Read(key);
                    var elapsed = Stopwatch.GetTimestamp() - started;
                    if (_sampleCount < _samples.Length)
                    {
                        _samples[_sampleCount++] = elapsed;
                    }
                    else
                    {
                        _missedSamples++;
                    }
                }
                else
                {
                    _checksum ^= _records.Read(key);
                }

                _reads++;
            }
        }
        catch (Exception exception)
        {
            _failure = exception;
        }
        finally
        {
            _ended = Stopwatch.GetTimestamp();
        }
    }

    public ReadMeasurement Finish()
    {
        Dispose();
        if (_failure is not null)
        {
            throw new InvalidOperationException("Native reader failed.", _failure);
        }

        var seconds = (_ended - _started) / (double)Stopwatch.Frequency;
        return new ReadMeasurement(_reads, seconds, _reads / seconds,
            LatencySummary.FromTicks(_samples.Take(_sampleCount)), _missedSamples, _checksum);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
