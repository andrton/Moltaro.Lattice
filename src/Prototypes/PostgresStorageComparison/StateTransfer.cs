using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class StateTransfer(string connectionString, int chunkCount)
{
    public const int ChunkBytes = 1024 * 1024;
    private readonly List<byte[]> _hashes = [];

    public double Seed()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var durability = new NpgsqlCommand("SET synchronous_commit = on", connection);
        durability.ExecuteNonQuery();
        var buffer = new byte[ChunkBytes];
        // Synthetic random bytes avoid a misleading all-zero compression advantage.
        var random = new Random(20261112);
        var started = Stopwatch.GetTimestamp();
        using var importer = connection.BeginBinaryImport("COPY checkpoint_chunks FROM STDIN (FORMAT BINARY)");
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            random.NextBytes(buffer);
            _hashes.Add(SHA256.HashData(buffer));
            importer.StartRow();
            importer.Write(chunk, NpgsqlDbType.Integer);
            importer.Write(buffer, NpgsqlDbType.Bytea);
        }

        importer.Complete();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public LoadMeasurement Load(bool useCopy)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        using var image = new NativeImage(chunkCount * ChunkBytes);
        if (useCopy)
        {
            using var exporter = connection.BeginBinaryExport(
                "COPY (SELECT chunk_id, payload FROM checkpoint_chunks ORDER BY chunk_id) TO STDOUT (FORMAT BINARY)");
            var chunk = 0;
            while (exporter.StartRow() != -1)
            {
                if (exporter.Read<int>() != chunk || chunk >= chunkCount)
                {
                    throw new InvalidDataException("Unexpected COPY chunk identity.");
                }

                var bytes = exporter.Read<byte[]>();
                if (bytes.Length != ChunkBytes)
                {
                    throw new InvalidDataException("Unexpected COPY chunk size.");
                }

                bytes.CopyTo(image.Slice(chunk * ChunkBytes, ChunkBytes));
                chunk++;
            }

            RequireCount(chunk);
        }
        else
        {
            using var command = new NpgsqlCommand(
                "SELECT chunk_id, payload FROM checkpoint_chunks ORDER BY chunk_id", connection);
            using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
            var chunk = 0;
            while (reader.Read())
            {
                if (reader.GetInt32(0) != chunk || chunk >= chunkCount)
                {
                    throw new InvalidDataException("Unexpected streamed chunk identity.");
                }

                using var stream = reader.GetStream(1);
                stream.ReadExactly(image.Slice(chunk * ChunkBytes, ChunkBytes));
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("Unexpected streamed chunk size.");
                }

                chunk++;
            }

            RequireCount(chunk);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var hash = SHA256.HashData(image.Slice(chunk * ChunkBytes, ChunkBytes));
            if (!hash.AsSpan().SequenceEqual(_hashes[chunk]))
            {
                throw new InvalidDataException("Loaded native image hash mismatch.");
            }
        }

        return new LoadMeasurement(useCopy ? "BinaryCopyByteArray" : "SequentialGetStream",
            chunkCount * ChunkBytes, elapsed, chunkCount / (elapsed / 1000), allocated,
            process.PeakWorkingSet64, true);
    }

    private void RequireCount(int count)
    {
        if (count != chunkCount)
        {
            throw new InvalidDataException("Incomplete state image.");
        }
    }
}
