using System.Data;
using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class CurrentStateStore : ICommitSink
{
    #region Scenario ownership and preparation

    private readonly string _connectionString;
    private readonly PersistenceMethodEnum _method;
    private readonly StorageUnitImage _image;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlCommand _update;
    private readonly PostgresCommitSink? _journal;
    private readonly byte[][] _buffers;
    private readonly long _relationBytesBefore;
    private bool[]? _acknowledged;
    private double _recoveryMilliseconds;

    public CurrentStateStore(string connectionString, PersistenceMethodEnum method, int unitBytes,
        bool disableCompression)
    {
        _connectionString = connectionString;
        _method = method;
        _image = new StorageUnitImage(unitBytes, initialize: true);
        _buffers = Enumerable.Range(0, method == PersistenceMethodEnum.Journal ? 1 : 64)
            .Select(_ => new byte[unitBytes]).ToArray();
        _connection = new NpgsqlConnection(connectionString);
        _update = new NpgsqlCommand { Connection = _connection };
        try
        {
            _connection.Open();
            using var setup = new NpgsqlCommand("""
                SET synchronous_commit = on;
                DROP TABLE IF EXISTS current_units;
                CREATE TABLE current_units (unit_id integer PRIMARY KEY, payload bytea NOT NULL);
                TRUNCATE change_batches
                """, _connection);
            setup.ExecuteNonQuery();
            if (disableCompression)
            {
                using var storage = new NpgsqlCommand(
                    "ALTER TABLE current_units ALTER COLUMN payload SET STORAGE EXTERNAL", _connection);
                storage.ExecuteNonQuery();
            }

            CopyImage();

            _update.CommandText = method == PersistenceMethodEnum.Update
                ? """
                    UPDATE current_units AS target SET payload = source.payload
                    FROM unnest($1::integer[], $2::bytea[]) AS source(unit_id, payload)
                    WHERE target.unit_id = source.unit_id
                    """
                : """
                    INSERT INTO current_units (unit_id, payload)
                    SELECT * FROM unnest($1::integer[], $2::bytea[])
                    ON CONFLICT (unit_id) DO UPDATE SET payload = EXCLUDED.payload
                    """;
            _update.Parameters.Add(new NpgsqlParameter<int[]> { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
            _update.Parameters.Add(new NpgsqlParameter<byte[][]> { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            _update.Prepare();
            if (method == PersistenceMethodEnum.Journal)
            {
                _journal = new PostgresCommitSink(connectionString);
            }

            _relationBytesBefore = RelationBytes();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public long WritePayloadBytes { get; private set; }
    public long UpdatedUnits { get; private set; }
    public long BufferCapacity => (long)_buffers.Length * _image.UnitBytes;

    #endregion

    #region Writes

    public void Commit(EncodedBatch batch)
    {
        Span<int> changed = stackalloc int[64];
        if (_journal is not null)
        {
            _journal.Commit(batch);
            _image.Apply(batch, changed);
            WritePayloadBytes += batch.Payload.Length;
            return;
        }

        // This private image is not visible to readers. A failed commit aborts the experiment.
        // The shared native read model is published by WriteExperiment only after this returns.
        var count = _image.Apply(batch, changed);
        var ids = changed[..count].ToArray();
        var payloads = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            _image.Unit(ids[index]).CopyTo(_buffers[index]);
            payloads[index] = _buffers[index];
        }

        ((NpgsqlParameter<int[]>)_update.Parameters[0]).TypedValue = ids;
        ((NpgsqlParameter<byte[][]>)_update.Parameters[1]).TypedValue = payloads;
        if (_update.ExecuteNonQuery() != count)
        {
            throw new InvalidDataException("Not every modified unit was persisted.");
        }

        WritePayloadBytes += (long)count * _image.UnitBytes;
        UpdatedUnits += count;
    }

    private void CopyImage()
    {
        using var importer = _connection.BeginBinaryImport(
            "COPY current_units (unit_id, payload) FROM STDIN (FORMAT BINARY)");
        for (var unit = 0; unit < _image.UnitCount; unit++)
        {
            _image.Unit(unit).CopyTo(_buffers[0]);
            importer.StartRow();
            importer.Write(unit, NpgsqlDbType.Integer);
            importer.Write(_buffers[0], NpgsqlDbType.Bytea);
        }

        importer.Complete();
    }

    #endregion

    #region Recovery and paused maintenance

    public void Verify(bool[] acknowledged, NativeRecords records)
    {
        _acknowledged = acknowledged;
        _recoveryMilliseconds = RecoverAndVerify(acknowledged, records);
    }

    private double RecoverAndVerify(bool[] acknowledged, NativeRecords records, bool verifyJournalMembership = true)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        var started = Stopwatch.GetTimestamp();
        using var recovered = new StorageUnitImage(_image.UnitBytes, initialize: false);
        using (var command = new NpgsqlCommand("SELECT unit_id, payload FROM current_units ORDER BY unit_id", connection))
        using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            var expectedUnit = 0;
            while (reader.Read())
            {
                if (reader.GetInt32(0) != expectedUnit || expectedUnit >= recovered.UnitCount)
                {
                    throw new InvalidDataException("Current image has missing or unexpected units.");
                }

                using var stream = reader.GetStream(1);
                stream.ReadExactly(recovered.Unit(expectedUnit++));
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("Current unit has an unexpected length.");
                }
            }

            if (expectedUnit != recovered.UnitCount)
            {
                throw new InvalidDataException("Current image is incomplete.");
            }
        }

        if (_journal is not null)
        {
            Span<int> changed = stackalloc int[64];
            foreach (var batch in _journal.ReadPersisted())
            {
                recovered.Apply(batch, changed);
            }
        }

        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        recovered.Verify(acknowledged, records, _image);
        if (_journal is not null && verifyJournalMembership)
        {
            // Exact acknowledged membership/order is checked before the journal is compacted.
            _journal.Verify(acknowledged, records);
        }

        return milliseconds;
    }

    public StorageMaintenanceMeasurement Maintain(DatabaseFixture database)
    {
        if (_acknowledged is null)
        {
            throw new InvalidOperationException("Verify the write run before maintenance.");
        }

        var afterWrites = RelationBytes();
        var walBefore = database.ReadWalPosition();
        var compactionMilliseconds = 0.0;
        var allocated = 0L;
        var afterRecovery = _recoveryMilliseconds;
        if (_method == PersistenceMethodEnum.Journal)
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            // Writes and readers of the SQL representation are stopped for this maintenance step.
            // PostgreSQL atomically commits the replacement base and removal of its covered tail.
            using (var transaction = _connection.BeginTransaction())
            {
                using var clear = new NpgsqlCommand("TRUNCATE current_units, change_batches", _connection, transaction);
                clear.ExecuteNonQuery();
                CopyImage();
                transaction.Commit();
            }

            compactionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            using var records = new NativeRecords();
            for (var sequence = 1; sequence < _acknowledged.Length; sequence++)
            {
                if (_acknowledged[sequence])
                {
                    records.Apply(new Mutation(sequence, 0));
                }
            }

            // The covered journal must be empty; recovery now verifies just the replacement base.
            if (_journal!.ReadPersisted().Any())
            {
                throw new InvalidDataException("Compaction retained its covered journal.");
            }

            // Do not demand historical journal membership after its safe deletion.
            afterRecovery = RecoverAndVerify(_acknowledged, records, verifyJournalMembership: false);
        }

        var compactWal = database.ReadCounters(walBefore).WalBytes;
        var vacuumStarted = Stopwatch.GetTimestamp();
        using (var vacuum = new NpgsqlCommand("VACUUM (ANALYZE) current_units, change_batches", _connection))
        {
            vacuum.ExecuteNonQuery();
        }

        return new StorageMaintenanceMeasurement(compactionMilliseconds,
            _method == PersistenceMethodEnum.Journal ? StorageUnitImage.ImageBytes : 0,
            allocated, compactWal, Stopwatch.GetElapsedTime(vacuumStarted).TotalMilliseconds,
            _relationBytesBefore, afterWrites, RelationBytes(), _recoveryMilliseconds, afterRecovery);
    }

    public void VerifyRollbackAndInsert()
    {
        if (_method == PersistenceMethodEnum.Journal)
        {
            return;
        }

        using (var transaction = _connection.BeginTransaction())
        {
            _update.Transaction = transaction;
            var id = _method == PersistenceMethodEnum.Upsert ? _image.UnitCount : 0;
            ((NpgsqlParameter<int[]>)_update.Parameters[0]).TypedValue = [id];
            ((NpgsqlParameter<byte[][]>)_update.Parameters[1]).TypedValue = [[1, 2, 3]];
            if (_update.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("The rollback probe did not modify its target.");
            }

            transaction.Rollback();
        }

        _update.Transaction = null;
        using var reopened = new NpgsqlConnection(_connectionString);
        reopened.Open();
        using var read = new NpgsqlCommand("SELECT payload FROM current_units WHERE unit_id = 0", reopened);
        if (!((byte[])read.ExecuteScalar()!).AsSpan().SequenceEqual(_image.Unit(0)))
        {
            throw new InvalidDataException("Rolled-back update survived reconnect.");
        }

        using var count = new NpgsqlCommand("SELECT count(*) FROM current_units", reopened);
        if ((long)count.ExecuteScalar()! != _image.UnitCount)
        {
            throw new InvalidDataException("Rolled-back insertion survived reconnect.");
        }
    }

    private long RelationBytes()
    {
        using var command = new NpgsqlCommand(
            "SELECT pg_total_relation_size('current_units') + pg_total_relation_size('change_batches')", _connection);
        return (long)command.ExecuteScalar()!;
    }

    #endregion

    public void Dispose()
    {
        _journal?.Dispose();
        _update.Dispose();
        _connection.Dispose();
        _image.Dispose();
    }
}
