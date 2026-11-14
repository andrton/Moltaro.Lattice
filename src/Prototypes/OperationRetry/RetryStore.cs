using Npgsql;

namespace Moltaro.Lattice.Prototypes.OperationRetry;

internal sealed class RetryStore(PgRetryScope scope, string stream)
{
    #region Setup and serialization

    public void Initialize(Guid history)
    {
        using var connection = scope.Open();
        using var command = new NpgsqlCommand($"INSERT INTO {scope.Schema}.control(stream,history) VALUES($1,$2)", connection);
        command.Parameters.AddWithValue(stream);
        command.Parameters.AddWithValue(history);
        command.ExecuteNonQuery();
    }

    // All protocol transitions acquire the control row before epoch/receipt rows.
    // This models the authoritative ordered commit stage, not an arena lock.
    // The injected seconds clock makes expiry checks deterministic without sleeping.
    private T Transaction<T>(long requestedTime, Func<NpgsqlConnection, NpgsqlTransaction, long, T> action)
    {
        using var connection = scope.Open();
        using var transaction = connection.BeginTransaction();
        using var clock = Command(connection, transaction,
            "UPDATE {schema}.control SET clock_floor=greatest(clock_floor,$2) WHERE stream=$1 RETURNING clock_floor",
            requestedTime);
        var now = (long)(clock.ExecuteScalar() ?? throw new InvalidOperationException("Unknown stream."));
        var result = action(connection, transaction, now);
        transaction.Commit();
        return result;
    }

    private NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql.Replace("{schema}", scope.Schema), connection, transaction);
        command.Parameters.AddWithValue(stream);
        foreach (var value in values)
        {
            command.Parameters.AddWithValue(value);
        }

        return command;
    }

    public long CreateEpoch(long now, long window = 900)
    {
        return Transaction(now, (connection, transaction, time) =>
        {
            using var close = Command(connection, transaction,
                "UPDATE {schema}.epochs SET accept_until=least(accept_until,$2) WHERE stream=$1", time);
            close.ExecuteNonQuery();
            using var next = Command(connection, transaction,
                "UPDATE {schema}.control SET next_epoch=next_epoch+1 WHERE stream=$1 RETURNING next_epoch");
            var epoch = (long)next.ExecuteScalar()!;
            using var insert = Command(connection, transaction,
                "INSERT INTO {schema}.epochs VALUES($1,$2,$3,$4,$5)", epoch, time + 60, window, time + 60);
            insert.ExecuteNonQuery();
            return epoch;
        });
    }

    #endregion

    #region Admission and effects

    public OperationReceipt Admit(OperationToken token, RetryRequest request, long now)
    {
        return Transaction(now, (connection, transaction, time) =>
        {
            var found = Find(connection, transaction, token, request.Fingerprint(), time);
            if (found.State != "Unknown")
            {
                return found;
            }

            using var epoch = Command(connection, transaction,
                "SELECT accept_until,window_seconds FROM {schema}.epochs WHERE stream=$1 AND epoch=$2", token.Epoch);
            long expires;
            using (var reader = epoch.ExecuteReader())
            {
                if (!reader.Read() || time >= reader.GetInt64(0))
                {
                    return new OperationReceipt("AdmissionClosed", 0, 0, "");
                }

                expires = checked(time + reader.GetInt64(1));
            }

            using var insert = Command(connection, transaction,
                "INSERT INTO {schema}.receipts(stream,epoch,operation,fingerprint,admitted,expires,state) VALUES($1,$2,$3,$4,$5,$6,'Admitted')",
                token.Epoch, token.Operation, request.Fingerprint(), time, expires);
            insert.ExecuteNonQuery();
            using var retain = Command(connection, transaction,
                "UPDATE {schema}.epochs SET retain_until=greatest(retain_until,$3) WHERE stream=$1 AND epoch=$2",
                token.Epoch, expires);
            retain.ExecuteNonQuery();
            return new OperationReceipt("Admitted", expires, 0, "");
        });
    }

    public OperationReceipt Execute(OperationToken token, RetryRequest request, long now,
        Action? beforeCommit = null, Action? afterCommit = null, Action? afterFirstEffect = null)
    {
        var receipt = Transaction(now, (connection, transaction, time) =>
        {
            var current = Find(connection, transaction, token, request.Fingerprint(), time);
            if (current.State != "Admitted")
            {
                return current;
            }

            var changed = Apply(connection, transaction, request.FirstKey, request);
            afterFirstEffect?.Invoke();
            if (request.SecondKey != 0)
            {
                changed += Apply(connection, transaction, request.SecondKey, request);
            }

            using var advance = Command(connection, transaction,
                "UPDATE {schema}.control SET position=position+1 WHERE stream=$1 RETURNING position");
            var position = (long)advance.ExecuteScalar()!;
            using var journal = Command(connection, transaction,
                "INSERT INTO {schema}.journal VALUES($1,$2,$3)", position, request.Encode());
            journal.ExecuteNonQuery();
            var result = changed == 0 ? "Unchanged" : "Changed";
            using var complete = Command(connection, transaction,
                "UPDATE {schema}.receipts SET state='Committed',position=$4,result=$5 WHERE stream=$1 AND epoch=$2 AND operation=$3",
                token.Epoch, token.Operation, position, result);
            complete.ExecuteNonQuery();
            beforeCommit?.Invoke();
            return new OperationReceipt("Committed", current.Expires, position, result);
        });
        afterCommit?.Invoke();
        return receipt;
    }

    private int Apply(NpgsqlConnection connection, NpgsqlTransaction transaction, int key, RetryRequest request)
    {
        if (request.Delete)
        {
            using var remove = Command(connection, transaction,
                "DELETE FROM {schema}.objects WHERE stream=$1 AND key=$2", key);
            return remove.ExecuteNonQuery();
        }

        using var add = Command(connection, transaction,
            "INSERT INTO {schema}.objects VALUES($1,$2,$3) ON CONFLICT(stream,key) DO UPDATE SET value={schema}.objects.value+EXCLUDED.value",
            key, request.Delta);
        return add.ExecuteNonQuery();
    }

    public OperationReceipt Reject(OperationToken token, RetryRequest request, long now)
    {
        return Transaction(now, (connection, transaction, time) =>
        {
            var current = Find(connection, transaction, token, request.Fingerprint(), time);
            if (current.State != "Admitted")
            {
                return current;
            }

            using var reject = Command(connection, transaction,
                "UPDATE {schema}.receipts SET state='Rejected',result='Validation' WHERE stream=$1 AND epoch=$2 AND operation=$3",
                token.Epoch, token.Operation);
            reject.ExecuteNonQuery();
            return current with { State = "Rejected", Result = "Validation" };
        });
    }

    #endregion

    #region Status, expiry and recovery

    public OperationReceipt Status(OperationToken token, long now) =>
        Transaction(now, (connection, transaction, time) => Find(connection, transaction, token, null, time));

    private OperationReceipt Find(NpgsqlConnection connection, NpgsqlTransaction transaction,
        OperationToken token, byte[]? fingerprint, long now)
    {
        using var metadata = Command(connection, transaction,
            "SELECT history,retired_epoch FROM {schema}.control WHERE stream=$1");
        using (var reader = metadata.ExecuteReader())
        {
            reader.Read();
            if (reader.GetGuid(0) != token.History)
            {
                return new OperationReceipt("HistoryMismatch", 0, 0, "");
            }

            if (token.Epoch <= reader.GetInt64(1))
            {
                return new OperationReceipt("Expired", 0, 0, "");
            }
        }

        using var query = Command(connection, transaction,
            "SELECT fingerprint,expires,state,position,result FROM {schema}.receipts WHERE stream=$1 AND epoch=$2 AND operation=$3",
            token.Epoch, token.Operation);
        using var receipt = query.ExecuteReader();
        if (!receipt.Read())
        {
            return new OperationReceipt("Unknown", 0, 0, "");
        }

        var expires = receipt.GetInt64(1);
        if (now >= expires)
        {
            return new OperationReceipt("Expired", expires, 0, "");
        }

        if (fingerprint is not null && !fingerprint.AsSpan().SequenceEqual(receipt.GetFieldValue<byte[]>(0)))
        {
            return new OperationReceipt("PayloadMismatch", expires, 0, "");
        }

        return new OperationReceipt(receipt.GetString(2), expires, receipt.GetInt64(3), receipt.GetString(4));
    }

    public bool Retire(long epoch, long now, Action? beforeCommit = null)
    {
        return Transaction(now, (connection, transaction, time) =>
        {
            using var check = Command(connection, transaction,
                "SELECT e.accept_until,e.retain_until,c.retired_epoch FROM {schema}.epochs e JOIN {schema}.control c USING(stream) WHERE e.stream=$1 AND e.epoch=$2",
                epoch);
            using (var reader = check.ExecuteReader())
            {
                if (!reader.Read() || time < reader.GetInt64(0) || time < reader.GetInt64(1) || epoch != reader.GetInt64(2) + 1)
                {
                    return false;
                }
            }

            using var retire = Command(connection, transaction,
                "UPDATE {schema}.control SET retired_epoch=$2 WHERE stream=$1", epoch);
            retire.ExecuteNonQuery();
            using var receipts = Command(connection, transaction,
                "DELETE FROM {schema}.receipts WHERE stream=$1 AND epoch=$2", epoch);
            receipts.ExecuteNonQuery();
            using var generation = Command(connection, transaction,
                "DELETE FROM {schema}.epochs WHERE stream=$1 AND epoch=$2", epoch);
            generation.ExecuteNonQuery();
            beforeCommit?.Invoke();
            return true;
        });
    }

    public void RestoreHistory(Guid history)
    {
        Transaction(0, (connection, transaction, time) =>
        {
            using var command = Command(connection, transaction,
                "UPDATE {schema}.control SET history=$2 WHERE stream=$1", history);
            return command.ExecuteNonQuery();
        });
    }

    public (int Total, int Roots, long Position, long Receipts, long Journal) Inspect()
    {
        return Transaction(0, (connection, transaction, time) =>
        {
            using var query = Command(connection, transaction, """
                SELECT coalesce((SELECT sum(value)::int FROM {schema}.objects WHERE stream=$1),0),
                    (SELECT count(*)::int FROM {schema}.objects WHERE stream=$1),
                    position,(SELECT count(*) FROM {schema}.receipts WHERE stream=$1),
                    (SELECT count(*) FROM {schema}.journal WHERE stream=$1)
                FROM {schema}.control WHERE stream=$1
                """);
            using var reader = query.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        });
    }

    #endregion
}
