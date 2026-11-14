using Npgsql;
using System.Data;
using System.Security.Cryptography;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed class PgBatchStore : IDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly string _schema;
    private readonly string _stream;

    public PgBatchStore(PgRunScope scope, string stream, SavedBaseKindEnum kind = SavedBaseKindEnum.FixtureImage)
    {
        _connection = scope.Open();
        _schema = scope.Schema;
        _stream = stream;
        Units = kind == SavedBaseKindEnum.Units256 ? new PgUnitBase(_connection, _schema, _stream) : null;
    }

    public PgUnitBase? Units { get; }

    public void Initialize(BatchMutation[] initial)
    {
        if (Units is not null)
        {
            Units.Initialize(initial);
            return;
        }
        using var command = new NpgsqlCommand($"INSERT INTO {_schema}.bases VALUES ($1,$2)", _connection);
        command.Parameters.AddWithValue(_stream);
        command.Parameters.AddWithValue(BatchCodec.Encode(initial));
        command.ExecuteNonQuery();
    }

    public byte[] ReadBase()
    {
        using var command = new NpgsqlCommand($"SELECT payload FROM {_schema}.bases WHERE stream=$1", _connection);
        command.Parameters.AddWithValue(_stream);
        return (byte[])(command.ExecuteScalar() ?? throw new InvalidOperationException("Missing base state."));
    }

    public RecoveryState ReadRecovery(Action? snapshotCaptured = null)
    {
        // Base, applied cursor and tail must come from one SQL snapshot. MVCC keeps
        // that snapshot valid while another connection applies and prunes rows.
        using var transaction = _connection.BeginTransaction(IsolationLevel.RepeatableRead);
        var state = Units is null ? RecoveryState.Initial(BatchCodec.Decode(ReadBase()))
            : Units.ReadBase(transaction, snapshotCaptured);
        var tail = ReadJournal(transaction, state.Position);
        transaction.Commit();
        return state with { Tail = tail };
    }

    public RetainedReceipt? Find(Guid operationId)
    {
        var sql = Units is null
            ? $"SELECT position,mutation_count,payload FROM {_schema}.journal WHERE stream=$1 AND operation_id=$2"
            : $"SELECT position,mutation_count,fingerprint FROM {_schema}.receipts WHERE stream=$1 AND operation_id=$2";
        using var command = new NpgsqlCommand(sql, _connection);
        command.Parameters.AddWithValue(_stream);
        command.Parameters.AddWithValue(operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var fingerprint = reader.GetFieldValue<byte[]>(2);
        return new RetainedReceipt(new BatchReceipt(reader.GetInt64(0), reader.GetInt32(1)),
            Units is null ? SHA256.HashData(fingerprint) : fingerprint);
    }

    public void Append(StoredBatch batch, Action<BatchPhaseEnum, int>? hook)
    {
        Check.That(batch.Receipt.Position is > 0 and <= 512, "Journal fixture limit reached before commit.");
        var commitAttempted = false;
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = new NpgsqlCommand($"INSERT INTO {_schema}.journal VALUES ($1,$2,$3,$4,$5)", _connection, transaction);
            command.Parameters.AddWithValue(_stream);
            command.Parameters.AddWithValue(batch.Receipt.Position);
            command.Parameters.AddWithValue(batch.OperationId);
            command.Parameters.AddWithValue(batch.Receipt.MutationCount);
            command.Parameters.AddWithValue(batch.Payload);
            command.ExecuteNonQuery();
            if (Units is not null)
            {
                using var receipt = new NpgsqlCommand($"INSERT INTO {_schema}.receipts VALUES ($1,$2,$3,$4,$5)", _connection, transaction);
                receipt.Parameters.AddWithValue(_stream);
                receipt.Parameters.AddWithValue(batch.OperationId);
                receipt.Parameters.AddWithValue(batch.Receipt.Position);
                receipt.Parameters.AddWithValue(batch.Receipt.MutationCount);
                receipt.Parameters.AddWithValue(SHA256.HashData(batch.Payload));
                receipt.ExecuteNonQuery();
            }
            hook?.Invoke(BatchPhaseEnum.BeforeCommit, 0);
            commitAttempted = true;
            transaction.Commit();
        }
        catch (Exception exception)
        {
            if (!commitAttempted)
            {
                transaction.Rollback();
            }

            throw new CommitFailureException(commitAttempted, exception);
        }
    }

    private StoredBatch[] ReadJournal(NpgsqlTransaction transaction, long after)
    {
        using var command = new NpgsqlCommand($"SELECT operation_id,position,mutation_count,payload FROM {_schema}.journal WHERE stream=$1 AND position>$2 ORDER BY position LIMIT 513", _connection, transaction);
        command.Parameters.AddWithValue(_stream);
        command.Parameters.AddWithValue(after);
        using var reader = command.ExecuteReader();
        var batches = new List<StoredBatch>();
        while (reader.Read())
        {
            Check.That(batches.Count < 512, "Journal fixture limit.");
            batches.Add(new StoredBatch(reader.GetGuid(0), new BatchReceipt(reader.GetInt64(1), reader.GetInt32(2)), reader.GetFieldValue<byte[]>(3)));
        }

        return batches.ToArray();
    }

    public long RowCount()
    {
        using var command = new NpgsqlCommand($"SELECT count(*) FROM {_schema}.journal WHERE stream=$1", _connection);
        command.Parameters.AddWithValue(_stream);
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose() => _connection.Dispose();
}
