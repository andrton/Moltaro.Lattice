using System.Data;
using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

// One background writer owns this connection and workspace. The foreground only appends journal rows.
internal sealed class BackgroundApplySession : IDisposable
{
    #region Connection and workspace ownership

    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlCommand _readJournal;
    private readonly NpgsqlCommand _readUnits;
    private readonly NpgsqlCommand _updateUnits;
    private readonly NpgsqlCommand _position;
    private readonly ReplayPatternEnum _pattern;
    private readonly List<long> _latencies = new();

    public BackgroundApplySession(string connectionString, int unitBytes, int maximumBatches, ReplayPatternEnum pattern, ReplayLayout layout)
    {
        _pattern = pattern;
        MaximumBatches = maximumBatches;
        var before = GC.GetAllocatedBytesForCurrentThread();
        Workspace = new ApplyWorkspace(layout, unitBytes, maximumBatches);
        WorkspaceInitialAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        _connection = new NpgsqlConnection(connectionString);
        try
        {
            _connection.Open();
            using (var settings = new NpgsqlCommand("SET synchronous_commit = on", _connection))
            {
                settings.ExecuteNonQuery();
            }

            using (var position = new NpgsqlCommand("SELECT sequence, mutation_count FROM applied_position WHERE id = 1", _connection))
            using (var reader = position.ExecuteReader())
            {
                reader.Read();
                Progress = new ApplyProgress(reader.GetInt64(0), reader.GetInt64(1));
            }

            _readJournal = new NpgsqlCommand("""
                SELECT first_sequence, mutation_count, payload FROM change_batches
                WHERE first_sequence > $1 ORDER BY first_sequence LIMIT $2
                """, _connection);
            _readJournal.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint });
            _readJournal.Parameters.AddWithValue(maximumBatches);
            _readJournal.Prepare();
            _readUnits = new NpgsqlCommand("SELECT unit_id, payload FROM apply_units WHERE unit_id = ANY($1)", _connection);
            _readUnits.Parameters.Add(new NpgsqlParameter<int[]> { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
            _readUnits.Prepare();
            _updateUnits = new NpgsqlCommand("""
                UPDATE apply_units AS target SET payload = source.payload
                FROM unnest($1::integer[], $2::bytea[]) AS source(unit_id, payload)
                WHERE target.unit_id = source.unit_id
                """, _connection);
            _updateUnits.Parameters.Add(new NpgsqlParameter<int[]> { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
            _updateUnits.Parameters.Add(new NpgsqlParameter<byte[][]> { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea });
            _updateUnits.Prepare();
            _position = new NpgsqlCommand("UPDATE applied_position SET sequence = $1, mutation_count = $2 WHERE id = 1", _connection);
            _position.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint });
            _position.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint });
            _position.Prepare();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public int MaximumBatches { get; }
    public ApplyWorkspace Workspace { get; }
    public long WorkspaceInitialAllocatedBytes { get; }
    public ApplyProgress Progress { get; private set; }
    public long UpdatedUnits { get; private set; }
    public long DistinctKeyWrites { get; private set; }
    public int Transactions { get; private set; }
    public int UnitBudgetSplits { get; private set; }
    public LatencySummary Latency => LatencySummary.FromTicks(_latencies);

    #endregion

    #region Ordered application and atomic progress

    public bool ApplyOnce(CancellationToken token, bool rollBack = false)
    {
        token.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        Workspace.Reset();
        ((NpgsqlParameter<long>)_readJournal.Parameters[0]).TypedValue = Progress.Sequence;
        using (var reader = _readJournal.ExecuteReader())
        {
            while (reader.Read())
            {
                var batch = new EncodedBatch(reader.GetInt64(0), reader.GetInt32(1), reader.GetFieldValue<byte[]>(2));
                if (!Workspace.AddBatch(batch, _pattern))
                {
                    break;
                }
            }
        }

        if (Workspace.ChangeCount == 0)
        {
            return false;
        }

        var ids = Workspace.UnitIds.AsSpan(0, Workspace.UnitCount).ToArray();
        ((NpgsqlParameter<int[]>)_readUnits.Parameters[0]).TypedValue = ids;
        using (var reader = _readUnits.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            var count = 0;
            while (reader.Read())
            {
                var unitId = reader.GetInt32(0);
                using var stream = reader.GetStream(1);
                stream.ReadExactly(Workspace.BufferForUnit(unitId));
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("Unexpected stored unit length.");
                }

                count++;
            }

            if (count != Workspace.UnitCount)
            {
                throw new InvalidDataException("A referenced unit is missing.");
            }
        }

        var payloads = Workspace.ApplyAndGetPayloads();
        var next = new ApplyProgress(Workspace.Changes[Workspace.ChangeCount - 1].Sequence,
            Progress.Count + Workspace.ChangeCount);
        token.ThrowIfCancellationRequested();
        using (var transaction = _connection.BeginTransaction())
        {
            _updateUnits.Transaction = transaction;
            _position.Transaction = transaction;
            ((NpgsqlParameter<int[]>)_updateUnits.Parameters[0]).TypedValue = ids;
            ((NpgsqlParameter<byte[][]>)_updateUnits.Parameters[1]).TypedValue = payloads;
            if (_updateUnits.ExecuteNonQuery() != Workspace.UnitCount)
            {
                throw new InvalidDataException("Not every selected unit was updated.");
            }

            ((NpgsqlParameter<long>)_position.Parameters[0]).TypedValue = next.Sequence;
            ((NpgsqlParameter<long>)_position.Parameters[1]).TypedValue = next.Count;
            if (_position.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("Applied position is missing.");
            }

            if (rollBack)
            {
                transaction.Rollback();
            }
            else
            {
                transaction.Commit();
            }
        }

        _updateUnits.Transaction = null;
        _position.Transaction = null;
        if (!rollBack)
        {
            Progress = next;
            Transactions++;
            UpdatedUnits += Workspace.UnitCount;
            DistinctKeyWrites += Workspace.DistinctKeys;
            UnitBudgetSplits += Workspace.HitUnitBudget ? 1 : 0;
            _latencies.Add(Stopwatch.GetTimestamp() - started);
        }

        return true;
    }

    #endregion

    public void Dispose()
    {
        _position.Dispose();
        _updateUnits.Dispose();
        _readUnits.Dispose();
        _readJournal.Dispose();
        _connection.Dispose();
    }
}
