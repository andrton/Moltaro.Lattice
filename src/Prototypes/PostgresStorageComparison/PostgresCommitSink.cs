using Npgsql;
using NpgsqlTypes;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class PostgresCommitSink : ICommitSink
{
    private readonly string _connectionString;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlCommand _insert;

    public PostgresCommitSink(string connectionString)
    {
        _connectionString = connectionString;
        _connection = new NpgsqlConnection(connectionString);
        try
        {
            _connection.Open();
            using var setup = new NpgsqlCommand("SET synchronous_commit = on; TRUNCATE change_batches", _connection);
            setup.ExecuteNonQuery();
            _insert = new NpgsqlCommand("INSERT INTO change_batches VALUES ($1, $2, $3)", _connection);
            _insert.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint });
            _insert.Parameters.Add(new NpgsqlParameter<int> { NpgsqlDbType = NpgsqlDbType.Integer });
            _insert.Parameters.Add(new NpgsqlParameter<byte[]> { NpgsqlDbType = NpgsqlDbType.Bytea });
            _insert.Prepare();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public void Commit(EncodedBatch batch)
    {
        ((NpgsqlParameter<long>)_insert.Parameters[0]).TypedValue = batch.FirstSequence;
        ((NpgsqlParameter<int>)_insert.Parameters[1]).TypedValue = batch.Count;
        ((NpgsqlParameter<byte[]>)_insert.Parameters[2]).TypedValue = batch.Payload;
        // An autocommit INSERT is one transaction for the entire encoded batch.
        _insert.ExecuteNonQuery();
    }

    public IEnumerable<EncodedBatch> ReadPersisted()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            "SELECT first_sequence, mutation_count, payload FROM change_batches ORDER BY first_sequence", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new EncodedBatch(reader.GetInt64(0), reader.GetInt32(1), reader.GetFieldValue<byte[]>(2));
        }
    }

    public void Verify(bool[] acknowledged, NativeRecords records)
    {
        BatchCodec.Verify(ReadPersisted(), acknowledged, records);
    }

    public void Dispose()
    {
        _insert.Dispose();
        _connection.Dispose();
    }
}
