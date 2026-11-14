using Npgsql;

namespace Moltaro.Lattice.Prototypes.OperationRetry;

internal sealed class PgRetryScope
{
    private readonly string _connectionString;

    public PgRetryScope(string connectionFile, Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A run ownership token is required.");
        }

        Schema = "lattice_retry_" + runId.ToString("N");
        var builder = new NpgsqlConnectionStringBuilder(File.ReadAllText(connectionFile).Trim());
        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            throw new InvalidOperationException("Only local PostgreSQL is allowed.");
        }

        builder.Database = "postgres";
        builder.ApplicationName = Schema;
        builder.Pooling = true;
        builder.MaxPoolSize = 4;
        builder.Timeout = 3;
        builder.CommandTimeout = 3;
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
                "SET synchronous_commit=on; SET statement_timeout='3s'; SET lock_timeout='2s'", connection);
            settings.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Setup()
    {
        using var connection = Open();
        using var durable = new NpgsqlCommand(
            "SELECT current_setting('fsync')='on' AND current_setting('full_page_writes')='on' AND current_setting('synchronous_commit')='on'", connection);
        if (!(bool)durable.ExecuteScalar()!)
        {
            throw new InvalidOperationException("Durable settings required.");
        }

        using var command = new NpgsqlCommand($"""
            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}.control (
                stream text PRIMARY KEY, history uuid NOT NULL, clock_floor bigint NOT NULL DEFAULT 0,
                next_epoch bigint NOT NULL DEFAULT 0, retired_epoch bigint NOT NULL DEFAULT 0,
                position bigint NOT NULL DEFAULT 0);
            CREATE TABLE {Schema}.epochs (
                stream text NOT NULL, epoch bigint NOT NULL, accept_until bigint NOT NULL,
                window_seconds bigint NOT NULL, retain_until bigint NOT NULL,
                PRIMARY KEY(stream,epoch));
            CREATE TABLE {Schema}.receipts (
                stream text NOT NULL, epoch bigint NOT NULL, operation uuid NOT NULL,
                fingerprint bytea NOT NULL CHECK(octet_length(fingerprint)=32),
                admitted bigint NOT NULL, expires bigint NOT NULL, state text NOT NULL,
                position bigint NOT NULL DEFAULT 0, result text NOT NULL DEFAULT '',
                PRIMARY KEY(stream,epoch,operation));
            CREATE TABLE {Schema}.objects (
                stream text NOT NULL, key integer NOT NULL, value integer NOT NULL,
                PRIMARY KEY(stream,key));
            CREATE TABLE {Schema}.journal (
                stream text NOT NULL, position bigint NOT NULL, payload text NOT NULL,
                PRIMARY KEY(stream,position));
            """, connection);
        command.ExecuteNonQuery();
    }

    public bool HasLockWaiter()
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=$1 AND wait_event_type='Lock')", connection);
        command.Parameters.AddWithValue(Schema);
        return (bool)command.ExecuteScalar()!;
    }

    public void Cleanup()
    {
        using var connection = Open();
        using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE", connection);
        drop.ExecuteNonQuery();
        using var verify = new NpgsqlCommand("SELECT count(*) FROM pg_namespace WHERE nspname=$1", connection);
        verify.Parameters.AddWithValue(Schema);
        if ((long)verify.ExecuteScalar()! != 0)
        {
            throw new InvalidOperationException("Owned schema was not removed.");
        }
    }
}
