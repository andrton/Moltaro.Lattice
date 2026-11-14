using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed class PgUnitBase(NpgsqlConnection connection, string schema, string stream)
{
    public const int MaximumBatches = 2;
    public const int JournalByteBudget = 64 * 1024;
    public const int UnitByteBudget = 128 * 1024;

    #region Base format and consistent recovery

    public void Initialize(BatchMutation[] initial)
    {
        using var transaction = connection.BeginTransaction();
        var roots = RecoveryState.Initial(initial).Roots;
        ReplaceUnits(transaction, roots.Select(root => root.Mutation.Identity.Slot).ToArray(),
            roots.SelectMany(UnitCodec.Encode).ToArray());
        using var cursor = new NpgsqlCommand($"INSERT INTO {schema}.unit_progress VALUES ($1,0,$2)", connection, transaction);
        cursor.Parameters.AddWithValue(stream);
        cursor.Parameters.AddWithValue((long)initial.Length);
        cursor.ExecuteNonQuery();
        transaction.Commit();
    }

    public RecoveryState ReadBase(NpgsqlTransaction transaction, Action? snapshotCaptured)
    {
        var progress = ReadProgress(transaction, false);
        snapshotCaptured?.Invoke();
        using var command = new NpgsqlCommand($"SELECT slot,part,payload FROM {schema}.saved_units WHERE stream=$1 ORDER BY slot,part", connection, transaction);
        command.Parameters.AddWithValue(stream);
        using var reader = command.ExecuteReader();
        var roots = new List<SavedRoot>();
        var parts = new List<SavedUnit>(UnitCodec.MaximumParts);
        while (reader.Read())
        {
            var slot = reader.GetInt32(0);
            if (parts.Count > 0 && parts[0].Slot != slot)
            {
                roots.Add(UnitCodec.Decode(parts[0].Slot, parts));
                parts.Clear();
            }

            Check.That(roots.Count < 512 && parts.Count < UnitCodec.MaximumParts, "Recovery fixture bound exceeded.");
            parts.Add(new SavedUnit(slot, reader.GetInt32(1), reader.GetFieldValue<byte[]>(2)));
        }
        if (parts.Count > 0)
        {
            roots.Add(UnitCodec.Decode(parts[0].Slot, parts));
        }

        return new RecoveryState(roots.ToArray(), progress.Position, progress.Revision, []);
    }

    private (long Position, long Revision) ReadProgress(NpgsqlTransaction? transaction, bool forUpdate)
    {
        using var command = new NpgsqlCommand($"SELECT applied,revision FROM {schema}.unit_progress WHERE stream=$1" +
            (forUpdate ? " FOR UPDATE" : ""), connection, transaction);
        command.Parameters.AddWithValue(stream);
        using var reader = command.ExecuteReader();
        Check.That(reader.Read(), "Missing unit progress.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public long Applied => ReadProgress(null, false).Position;

    #endregion

    #region Bounded whole-transaction application

    public UnitApplySample ApplyOnce(Action<UnitApplyPhaseEnum>? hook = null)
    {
        var started = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        using var transaction = connection.BeginTransaction();
        // SQL cursor ownership excludes a second applier. It is never acquired by
        // foreground append and is unrelated to arena/root/publication protection.
        var progress = ReadProgress(transaction, true);
        var batches = ReadPrefix(transaction, progress.Position);
        if (batches.Count == 0)
        {
            transaction.Rollback();
            return default;
        }

        var final = new Dictionary<int, SavedRoot?>(MaximumBatches * BatchEngine.MaximumBatch);
        var revision = progress.Revision;
        var position = progress.Position;
        var mutations = 0;
        foreach (var batch in batches)
        {
            Check.That(batch.Receipt.Position == position + 1, "Applier encountered a journal gap.");
            var changes = BatchCodec.Decode(batch.Payload);
            Check.That(changes.Length == batch.Receipt.MutationCount && changes.Length <= BatchEngine.MaximumBatch,
                "Applier encountered an invalid batch.");
            foreach (var change in changes)
            {
                final[change.Identity.Slot] = change.Kind == MutationKindEnum.Delete
                    ? null : new SavedRoot(change, ++revision);
                mutations++;
            }
            position = batch.Receipt.Position;
        }

        var units = final.Values.Where(value => value.HasValue).SelectMany(value => UnitCodec.Encode(value!.Value)).ToArray();
        var unitBytes = units.Length * UnitCodec.Bytes;
        Check.That(unitBytes <= UnitByteBudget, "Applier unit workspace bound exceeded.");
        hook?.Invoke(UnitApplyPhaseEnum.PrefixRead);
        ReplaceUnits(transaction, final.Keys.ToArray(), units);
        hook?.Invoke(UnitApplyPhaseEnum.UnitsWritten);
        using (var cursor = new NpgsqlCommand($"UPDATE {schema}.unit_progress SET applied=$2,revision=$3 WHERE stream=$1", connection, transaction))
        {
            cursor.Parameters.AddWithValue(stream);
            cursor.Parameters.AddWithValue(position);
            cursor.Parameters.AddWithValue(revision);
            Check.That(cursor.ExecuteNonQuery() == 1, "Missing applier cursor update.");
        }
        hook?.Invoke(UnitApplyPhaseEnum.CursorWritten);
        transaction.Commit();
        hook?.Invoke(UnitApplyPhaseEnum.AppliedCommitted);
        return new UnitApplySample(progress.Position, position, batches.Count, mutations, final.Count, units.Length,
            batches.Sum(batch => batch.Payload.Length), unitBytes, Stopwatch.GetTimestamp() - started,
            GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    private List<StoredBatch> ReadPrefix(NpgsqlTransaction transaction, long after)
    {
        using var command = new NpgsqlCommand($"""
            SELECT operation_id,position,mutation_count,octet_length(payload),payload
            FROM {schema}.journal WHERE stream=$1 AND position>$2 ORDER BY position LIMIT {MaximumBatches}
            """, connection, transaction);
        command.Parameters.AddWithValue(stream);
        command.Parameters.AddWithValue(after);
        using var reader = command.ExecuteReader(System.Data.CommandBehavior.SequentialAccess);
        var batches = new List<StoredBatch>(MaximumBatches);
        var bytes = 0;
        while (reader.Read())
        {
            var token = reader.GetGuid(0);
            var position = reader.GetInt64(1);
            var count = reader.GetInt32(2);
            var length = reader.GetInt32(3);
            Check.That(length <= JournalByteBudget, "A single batch exceeds applier input budget.");
            if (bytes + length > JournalByteBudget)
            {
                break;
            }
            batches.Add(new StoredBatch(token, new BatchReceipt(position, count), reader.GetFieldValue<byte[]>(4)));
            bytes += length;
        }
        return batches;
    }

    private void ReplaceUnits(NpgsqlTransaction transaction, int[] slots, SavedUnit[] units)
    {
        using (var delete = new NpgsqlCommand($"DELETE FROM {schema}.saved_units WHERE stream=$1 AND slot=ANY($2)", connection, transaction))
        {
            delete.Parameters.AddWithValue(stream);
            delete.Parameters.AddWithValue(slots);
            delete.ExecuteNonQuery();
        }
        if (units.Length == 0)
        {
            return;
        }

        using var insert = new NpgsqlCommand($"""
            INSERT INTO {schema}.saved_units (stream,slot,part,payload)
            SELECT $1,slot,part,payload FROM unnest($2::integer[],$3::integer[],$4::bytea[]) AS u(slot,part,payload)
            """, connection, transaction);
        insert.Parameters.AddWithValue(stream);
        insert.Parameters.AddWithValue(units.Select(unit => unit.Slot).ToArray());
        insert.Parameters.AddWithValue(units.Select(unit => unit.Part).ToArray());
        insert.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Bytea, units.Select(unit => unit.Payload).ToArray());
        insert.ExecuteNonQuery();
    }

    #endregion

    #region Separate logical cleanup

    public int Cleanup(Action<UnitApplyPhaseEnum>? hook = null)
    {
        using var transaction = connection.BeginTransaction();
        var progress = ReadProgress(transaction, true);
        using var delete = new NpgsqlCommand($"DELETE FROM {schema}.journal WHERE stream=$1 AND position<=$2", connection, transaction);
        delete.Parameters.AddWithValue(stream);
        delete.Parameters.AddWithValue(progress.Position);
        var count = delete.ExecuteNonQuery();
        hook?.Invoke(UnitApplyPhaseEnum.CleanupDeleted);
        transaction.Commit();
        hook?.Invoke(UnitApplyPhaseEnum.CleanupCommitted);
        return count;
    }

    #endregion
}
