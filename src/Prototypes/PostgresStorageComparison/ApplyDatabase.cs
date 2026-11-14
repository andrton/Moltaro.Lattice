using System.Buffers.Binary;
using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class ApplyDatabase
{
    #region Saved state preparation

    public static void Seed(string connectionString, NativeImage image, int unitBytes, ReplayLayout layout)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using (var schema = new NpgsqlCommand("""
            SET synchronous_commit = on;
            DROP TABLE IF EXISTS apply_units, applied_position;
            CREATE TABLE apply_units (unit_id integer PRIMARY KEY, payload bytea NOT NULL);
            ALTER TABLE apply_units ALTER COLUMN payload SET STORAGE EXTERNAL;
            CREATE TABLE applied_position (id integer PRIMARY KEY, sequence bigint NOT NULL, mutation_count bigint NOT NULL);
            INSERT INTO applied_position VALUES (1, 0, 0);
            TRUNCATE change_batches
            """, connection))
        {
            schema.ExecuteNonQuery();
        }

        var buffer = new byte[unitBytes];
        using var importer = connection.BeginBinaryImport("COPY apply_units (unit_id, payload) FROM STDIN (FORMAT BINARY)");
        for (var unit = 0; unit < layout.ImageBytes / unitBytes; unit++)
        {
            image.Slice(unit * unitBytes, unitBytes).CopyTo(buffer);
            importer.StartRow();
            importer.Write(unit, NpgsqlDbType.Integer);
            importer.Write(buffer, NpgsqlDbType.Bytea);
        }

        importer.Complete();
    }

    #endregion

    #region Saved state and journal recovery verification

    public static ApplyProgress Verify(string connectionString, NativeImage image, NativeRecords records,
        int unitBytes, ReplayPatternEnum pattern, bool[] acknowledged, ReplayLayout layout, ApplyProgress? expectedPosition = null)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        // Verification runs after workload threads stop. One snapshot also ties the base to its cursor and journal.
        using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
        ApplyProgress position;
        using (var command = new NpgsqlCommand("SELECT sequence, mutation_count FROM applied_position WHERE id = 1", connection, transaction))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            position = new ApplyProgress(reader.GetInt64(0), reader.GetInt64(1));
        }

        if (expectedPosition is not null && position != expectedPosition)
        {
            throw new InvalidDataException("Stored progress differs from the committed worker position.");
        }

        var seen = new bool[acknowledged.Length];
        var baseValues = new long[layout.RecordCount];
        var recoveredValues = new long[layout.RecordCount];
        var appliedCount = 0L;
        var previous = 0L;
        var boundaryFound = position.Sequence == 0;
        using (var command = new NpgsqlCommand(
            "SELECT first_sequence, mutation_count, payload FROM change_batches ORDER BY first_sequence", connection, transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var first = reader.GetInt64(0);
                var count = reader.GetInt32(1);
                var payload = reader.GetFieldValue<byte[]>(2);
                if (count is < 1 or > ReplayLayout.WriterBatch || payload.Length != count * ReplayLayout.CommandBytes)
                {
                    throw new InvalidDataException("Invalid persisted journal batch.");
                }

                for (var index = 0; index < count; index++)
                {
                    var change = layout.Decode(payload.AsSpan(index * ReplayLayout.CommandBytes), pattern);
                    if (change.Sequence <= previous || change.Sequence >= acknowledged.Length
                        || !acknowledged[change.Sequence] || (index == 0 && change.Sequence != first))
                    {
                        throw new InvalidDataException("Journal membership/order differs from acknowledged writes.");
                    }

                    seen[change.Sequence] = true;
                    previous = change.Sequence;
                    recoveredValues[change.Key] = change.Sequence;
                    if (change.Sequence <= position.Sequence)
                    {
                        baseValues[change.Key] = change.Sequence;
                        appliedCount++;
                    }
                }

                boundaryFound |= previous == position.Sequence;
            }
        }

        if (!seen.AsSpan().SequenceEqual(acknowledged) || appliedCount != position.Count || !boundaryFound)
        {
            throw new InvalidDataException("Incorrect journal membership or applied boundary.");
        }

        var buffer = new byte[unitBytes];
        using (var command = new NpgsqlCommand("SELECT unit_id, payload FROM apply_units ORDER BY unit_id", connection, transaction))
        using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            var unit = 0;
            while (reader.Read())
            {
                if (reader.GetInt32(0) != unit)
                {
                    throw new InvalidDataException("Stored unit order/identity differs.");
                }

                using var stream = reader.GetStream(1);
                stream.ReadExactly(buffer);
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException("Stored unit size differs.");
                }

                for (var offset = 0; offset < unitBytes; offset += layout.RecordBytes)
                {
                    var key = (unit * unitBytes + offset) / layout.RecordBytes;
                    var bytes = buffer.AsSpan(offset, layout.RecordBytes);
                    if (BinaryPrimitives.ReadInt64LittleEndian(bytes) != baseValues[key]
                        || BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]) != ~baseValues[key]
                        || !bytes[16..].SequenceEqual(image.Slice(key * layout.RecordBytes + 16, layout.RecordBytes - 16)))
                    {
                        throw new InvalidDataException("Saved state does not match its applied position.");
                    }

                    // Verified base values plus the ordered tail must equal the latest resident values.
                    var latest = recoveredValues[key];
                    if (records.Read(key) != latest
                        || BinaryPrimitives.ReadInt64LittleEndian(image.Slice(key * layout.RecordBytes, 16)) != latest)
                    {
                        throw new InvalidDataException("Recovered state differs from native publication.");
                    }
                }

                unit++;
            }

            if (unit != layout.ImageBytes / unitBytes)
            {
                throw new InvalidDataException("Stored image is incomplete.");
            }
        }

        transaction.Commit();
        return position;
    }

    #endregion
}
