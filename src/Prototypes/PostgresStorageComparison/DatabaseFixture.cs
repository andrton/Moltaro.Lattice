using Npgsql;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class DatabaseFixture : IDisposable
{
    #region Owned database setup

    private readonly NpgsqlConnection _admin;
    private readonly string _name = "lattice_probe_" + Guid.NewGuid().ToString("N");
    private bool _created;

    public DatabaseFixture(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            throw new ArgumentException("This bounded prototype accepts a local PostgreSQL host only.");
        }

        builder.Database = "postgres";
        builder.Pooling = false;
        builder.Timeout = 5;
        builder.CommandTimeout = 20;
        builder.ApplicationName = "Moltaro.Lattice.PostgresStorageComparison";
        _admin = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            _admin.Open();
            Settings = ReadSettings();
            if (Settings["fsync"] != "on" || Settings["full_page_writes"] != "on")
            {
                throw new InvalidOperationException("The durable comparison requires fsync and full_page_writes enabled.");
            }

            using var create = new NpgsqlCommand($"CREATE DATABASE {_name}", _admin);
            create.ExecuteNonQuery();
            _created = true;
            builder.Database = _name;
            ConnectionString = builder.ConnectionString;
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();
            using var schema = new NpgsqlCommand("""
                CREATE TABLE change_batches (
                    first_sequence bigint PRIMARY KEY,
                    mutation_count integer NOT NULL,
                    payload bytea NOT NULL
                );
                CREATE TABLE checkpoint_chunks (
                    chunk_id integer PRIMARY KEY,
                    payload bytea NOT NULL
                )
                """, connection);
            schema.ExecuteNonQuery();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string ConnectionString { get; }

    public Dictionary<string, string> Settings { get; }

    #endregion

    #region Observations and verification

    private Dictionary<string, string> ReadSettings()
    {
        using var command = new NpgsqlCommand("""
            SELECT name, setting FROM pg_settings
            WHERE name IN ('server_version', 'fsync', 'full_page_writes', 'synchronous_commit',
                'wal_sync_method', 'shared_buffers', 'wal_compression', 'block_size',
                'autovacuum', 'checkpoint_timeout', 'max_wal_size', 'default_toast_compression')
            UNION ALL
            SELECT 'other_active_clients', count(*)::text FROM pg_stat_activity
            WHERE backend_type = 'client backend' AND state = 'active' AND pid <> pg_backend_pid()
            """, _admin);
        using var reader = command.ExecuteReader();
        var settings = new Dictionary<string, string>();
        while (reader.Read())
        {
            settings.Add(reader.GetString(0), reader.GetString(1));
        }

        return settings;
    }

    public string ReadWalPosition()
    {
        using var command = new NpgsqlCommand("SELECT pg_current_wal_insert_lsn()::text", _admin);
        return (string)command.ExecuteScalar()!;
    }

    public DatabaseCounters ReadCounters(string walStart)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(), $1::pg_lsn)::double precision,
                   pg_total_relation_size('change_batches')
            """, connection);
        command.Parameters.AddWithValue(walStart);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new DatabaseCounters(reader.GetDouble(0), reader.GetInt64(1));
    }

    public void VerifyRollback()
    {
        using (var connection = new NpgsqlConnection(ConnectionString))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = new NpgsqlCommand(
                "INSERT INTO change_batches VALUES (1, 1, decode('010203', 'hex'))", connection, transaction);
            insert.ExecuteNonQuery();
            transaction.Rollback();
        }

        using var reopened = new NpgsqlConnection(ConnectionString);
        reopened.Open();
        using var count = new NpgsqlCommand("SELECT count(*) FROM change_batches", reopened);
        if ((long)count.ExecuteScalar()! != 0)
        {
            throw new InvalidDataException("Rolled-back state survived reconnect.");
        }
    }

    #endregion

    #region Cleanup

    public void Dispose()
    {
        try
        {
            if (_created)
            {
                // The name is generated here, never accepted from user input.
                // Only this run's newly created database can be removed.
                using var drop = new NpgsqlCommand($"DROP DATABASE {_name} WITH (FORCE)", _admin);
                drop.ExecuteNonQuery();
                _created = false;
            }
        }
        finally
        {
            _admin.Dispose();
        }
    }

    #endregion
}
