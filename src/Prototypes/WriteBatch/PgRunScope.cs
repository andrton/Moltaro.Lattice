using Npgsql;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed class PgRunScope
{
    private readonly string _connectionString;

    public PgRunScope(string connectionFile, Guid runId)
    {
        Check.That(runId != Guid.Empty, "Run ownership token required.");
        Schema = "lattice_batch_" + runId.ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(File.ReadAllText(connectionFile).Trim());
        Check.That(builder.Host is "localhost" or "127.0.0.1" or "::1", "Only local PostgreSQL is allowed.");
        builder.Database = "postgres";
        builder.Pooling = false;
        builder.Timeout = 3;
        builder.CommandTimeout = 3;
        builder.ApplicationName = "Moltaro.Lattice.WriteBatch";
        builder.IncludeErrorDetail = false;
        _connectionString = builder.ConnectionString;
    }

    public string Schema { get; }

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            connection.Open();
            using var settings = new NpgsqlCommand(
                "SET synchronous_commit=on; SET statement_timeout='3s'; SET lock_timeout='1s';", connection);
            settings.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public Dictionary<string, string> Setup()
    {
        using var connection = Open();
        var result = new Dictionary<string, string>();
        using (var settings = new NpgsqlCommand("SELECT name, setting FROM pg_settings WHERE name IN ('server_version','fsync','full_page_writes','synchronous_commit')", connection))
        using (var reader = settings.ExecuteReader())
        {
            while (reader.Read())
            {
                result.Add(reader.GetString(0), reader.GetString(1));
            }
        }

        Check.That(result["fsync"] == "on" && result["full_page_writes"] == "on" && result["synchronous_commit"] == "on",
            "Durable settings are required.");
        using var create = new NpgsqlCommand($"""
            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}.bases (
                stream text PRIMARY KEY,
                payload bytea NOT NULL
            );
            CREATE TABLE {Schema}.journal (
                stream text NOT NULL,
                position bigint NOT NULL,
                operation_id uuid NOT NULL,
                mutation_count integer NOT NULL,
                payload bytea NOT NULL,
                PRIMARY KEY (stream, position),
                UNIQUE (stream, operation_id)
            );
            CREATE TABLE {Schema}.unit_progress (
                stream text PRIMARY KEY,
                applied bigint NOT NULL,
                revision bigint NOT NULL
            );
            CREATE TABLE {Schema}.saved_units (
                stream text NOT NULL,
                slot integer NOT NULL,
                part integer NOT NULL,
                payload bytea NOT NULL CHECK (octet_length(payload) = 256),
                PRIMARY KEY (stream, slot, part)
            );
            CREATE TABLE {Schema}.receipts (
                stream text NOT NULL,
                operation_id uuid NOT NULL,
                position bigint NOT NULL,
                mutation_count integer NOT NULL,
                fingerprint bytea NOT NULL CHECK (octet_length(fingerprint) = 32),
                PRIMARY KEY (stream, operation_id),
                UNIQUE (stream, position)
            );
            """, connection);
        create.ExecuteNonQuery();
        return result;
    }

    public void Cleanup()
    {
        using var connection = Open();
        // Only the schema derived from this runner's random ownership token.
        using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE", connection);
        drop.ExecuteNonQuery();
        using var verify = new NpgsqlCommand("SELECT count(*) FROM pg_namespace WHERE nspname=$1", connection);
        verify.Parameters.AddWithValue(Schema);
        Check.That((long)verify.ExecuteScalar()! == 0, "Owned schema cleanup failed.");
    }
}
