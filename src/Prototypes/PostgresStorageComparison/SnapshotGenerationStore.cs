using System.Buffers.Binary;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class SnapshotGenerationStore : IDisposable
{
    #region Ownership and setup

    public const int ChunkBytes = 1024 * 1024;
    public const int ChunksPerTransaction = 32;
    private readonly string _connectionString;
    private readonly NpgsqlConnection _connection;
    private readonly byte[] _buffer = new byte[ChunkBytes];

    public SnapshotGenerationStore(string connectionString)
    {
        _connectionString = connectionString;
        _connection = new NpgsqlConnection(connectionString);
        try
        {
            _connection.Open();
            using var setup = new NpgsqlCommand("""
                SET synchronous_commit = on;
                DROP TABLE IF EXISTS pause_chunks, pause_manifest;
                CREATE TABLE pause_chunks (
                    generation integer NOT NULL,
                    chunk_id integer NOT NULL,
                    checksum bytea NOT NULL,
                    payload bytea NOT NULL,
                    PRIMARY KEY (generation, chunk_id)
                );
                ALTER TABLE pause_chunks ALTER COLUMN payload SET STORAGE EXTERNAL;
                CREATE TABLE pause_manifest (
                    id integer PRIMARY KEY,
                    generation integer NOT NULL,
                    through_sequence bigint NOT NULL
                );
                INSERT INTO pause_manifest VALUES (1, -1, 0)
                """, _connection);
            setup.ExecuteNonQuery();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public static void InitializeImage(NativeImage image, int chunks)
    {
        var random = new Random(20261112);
        for (var chunk = 0; chunk < chunks; chunk++)
        {
            random.NextBytes(image.Slice(chunk * ChunkBytes, ChunkBytes));
        }

        for (var key = 0; key < NativeRecords.RecordCount; key++)
        {
            var record = image.Slice(key * 16, 16);
            BinaryPrimitives.WriteInt64LittleEndian(record, 0);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], ~0L);
        }
    }

    #endregion

    #region Candidate write and atomic publication

    public SnapshotGeneration WriteGeneration(NativeImage image, int chunks, int generation,
        long throughSequence, CancellationToken token, int? stopAfterChunks = null)
    {
        var started = Stopwatch.GetTimestamp();
        var count = Math.Min(chunks, stopAfterChunks ?? chunks);
        var hashes = new byte[count][];
        for (var first = 0; first < count; first += ChunksPerTransaction)
        {
            token.ThrowIfCancellationRequested();
            using var transaction = _connection.BeginTransaction();
            using (var importer = _connection.BeginBinaryImport(
                "COPY pause_chunks (generation, chunk_id, checksum, payload) FROM STDIN (FORMAT BINARY)"))
            {
                var end = Math.Min(first + ChunksPerTransaction, count);
                for (var chunk = first; chunk < end; chunk++)
                {
                    token.ThrowIfCancellationRequested();
                    image.Slice(chunk * ChunkBytes, ChunkBytes).CopyTo(_buffer);
                    hashes[chunk] = SHA256.HashData(_buffer);
                    importer.StartRow();
                    importer.Write(generation, NpgsqlDbType.Integer);
                    importer.Write(chunk, NpgsqlDbType.Integer);
                    importer.Write(hashes[chunk], NpgsqlDbType.Bytea);
                    importer.Write(_buffer, NpgsqlDbType.Bytea);
                }

                importer.Complete();
            }

            transaction.Commit();
        }

        return new SnapshotGeneration(generation, throughSequence, chunks, hashes,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0);
    }

    public SnapshotGeneration Publish(SnapshotGeneration generation, bool rollBack = false)
    {
        if (generation.Hashes.Length != generation.ExpectedChunks)
        {
            throw new InvalidOperationException("An incomplete generation cannot become active.");
        }

        var started = Stopwatch.GetTimestamp();
        using var transaction = _connection.BeginTransaction();
        using var publish = new NpgsqlCommand("""
            UPDATE pause_manifest SET generation = $1, through_sequence = $2
            WHERE id = 1 AND (SELECT count(*) FROM pause_chunks WHERE generation = $1) = $3
            """, _connection, transaction);
        publish.Parameters.AddWithValue(generation.Id);
        publish.Parameters.AddWithValue(generation.ThroughSequence);
        publish.Parameters.AddWithValue(generation.ExpectedChunks);
        if (publish.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("Generation publication did not find every expected chunk.");
        }

        if (rollBack)
        {
            transaction.Rollback();
        }
        else
        {
            transaction.Commit();
        }

        return generation with { PublicationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
    }

    #endregion

    #region Full readback and recovery verification

    public void VerifyRecovery(SnapshotGeneration expectedGeneration, PostgresCommitSink journal,
        bool[] acknowledged, int commandBytes, NativeRecords records, NativeImage image)
    {
        // Reopen independently of the writer and its prepared statements.
        using var reopened = new NpgsqlConnection(_connectionString);
        reopened.Open();
        using (var manifest = new NpgsqlCommand("SELECT generation, through_sequence FROM pause_manifest WHERE id = 1", reopened))
        using (var reader = manifest.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt32(0) != expectedGeneration.Id
                || reader.GetInt64(1) != expectedGeneration.ThroughSequence)
            {
                throw new InvalidDataException("Unexpected active base or committed boundary.");
            }
        }

        var recovered = new long[NativeRecords.RecordCount];
        using (var command = new NpgsqlCommand(
            "SELECT chunk_id, checksum, payload FROM pause_chunks WHERE generation = $1 ORDER BY chunk_id", reopened))
        {
            command.Parameters.AddWithValue(expectedGeneration.Id);
            using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess);
            var chunk = 0;
            while (reader.Read())
            {
                if (chunk >= expectedGeneration.ExpectedChunks || reader.GetInt32(0) != chunk)
                {
                    throw new InvalidDataException("Missing or unexpected base chunk.");
                }

                var storedHash = reader.GetFieldValue<byte[]>(1);
                using var stream = reader.GetStream(2);
                stream.ReadExactly(_buffer);
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("Unexpected base chunk length.");
                }

                var actualHash = SHA256.HashData(_buffer);
                if (!actualHash.AsSpan().SequenceEqual(storedHash)
                    || !actualHash.AsSpan().SequenceEqual(expectedGeneration.Hashes[chunk]))
                {
                    throw new InvalidDataException("Base chunk checksum mismatch.");
                }

                if (chunk == 0)
                {
                    for (var key = 0; key < recovered.Length; key++)
                    {
                        var record = _buffer.AsSpan(key * 16, 16);
                        recovered[key] = BinaryPrimitives.ReadInt64LittleEndian(record);
                        if (BinaryPrimitives.ReadInt64LittleEndian(record[8..]) != ~recovered[key])
                        {
                            throw new InvalidDataException("Torn base record.");
                        }
                    }
                }

                chunk++;
            }

            if (chunk != expectedGeneration.ExpectedChunks)
            {
                throw new InvalidDataException("Incomplete active base.");
            }
        }

        var expected = new long[NativeRecords.RecordCount];
        for (var sequence = 1L; sequence <= expectedGeneration.ThroughSequence; sequence++)
        {
            expected[Mutation.KeyFor(sequence)] = sequence;
        }

        if (!recovered.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException("The base does not represent its declared boundary.");
        }

        var seen = new bool[acknowledged.Length];
        long previous = 0;
        foreach (var batch in journal.ReadPersisted())
        {
            if (batch.Count is < 1 or > SnapshotPauseWorkload.MaximumBatch
                || batch.Payload.Length != batch.Count * commandBytes)
            {
                throw new InvalidDataException("Invalid queued journal batch size.");
            }

            for (var index = 0; index < batch.Count; index++)
            {
                var sequence = QueuedWriteCodec.Validate(batch.Payload.AsSpan(index * commandBytes, commandBytes));
                if (sequence <= previous || sequence >= acknowledged.Length || !acknowledged[sequence]
                    || (index == 0 && batch.FirstSequence != sequence))
                {
                    throw new InvalidDataException("Persisted/acknowledged command identity mismatch.");
                }

                seen[sequence] = true;
                previous = sequence;
                expected[Mutation.KeyFor(sequence)] = sequence;
                if (sequence > expectedGeneration.ThroughSequence)
                {
                    recovered[Mutation.KeyFor(sequence)] = sequence;
                }
            }
        }

        if (!seen.AsSpan().SequenceEqual(acknowledged))
        {
            throw new InvalidDataException("Journal membership differs from acknowledged writes.");
        }

        for (var key = 0; key < recovered.Length; key++)
        {
            var resident = image.Slice(key * 16, 16);
            if (recovered[key] != expected[key] || records.Read(key) != expected[key]
                || BinaryPrimitives.ReadInt64LittleEndian(resident) != expected[key]
                || BinaryPrimitives.ReadInt64LittleEndian(resident[8..]) != ~expected[key])
            {
                throw new InvalidDataException("Recovered state differs from native publication.");
            }
        }
    }

    public long RelationBytes()
    {
        using var command = new NpgsqlCommand("""
            SELECT pg_total_relation_size('pause_chunks') + pg_total_relation_size('pause_manifest')
                + pg_total_relation_size('change_batches')
            """, _connection);
        return (long)command.ExecuteScalar()!;
    }

    #endregion

    public void Dispose() => _connection.Dispose();
}
