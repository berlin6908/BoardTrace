using BoardTrace.Contracts;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Core;

public sealed partial class LocalInspectionStore
{
    private static void InitializeRuntime(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_Inspections_RuntimeCounts ON Inspections(
                json_extract(Document,'$.batchId'),json_extract(Document,'$.purpose'));
            CREATE INDEX IF NOT EXISTS IX_Inspections_RuntimeStarted ON Inspections(Id)
                WHERE json_extract(Document,'$.executionStatus')='Started';
            """;
        command.ExecuteNonQuery();
    }

    // Batch identity/counts and whole-station blockers come from the same WAL snapshot.
    // Started attempts count as accepted; no image BLOB is loaded for this report.
    public StationRuntimeUpdate ReadRuntime(string state, string? alarm = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var batch = ReadActiveBatch(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM Inspections WHERE json_extract(Document,'$.batchId')=$batch AND json_extract(Document,'$.purpose')='FirstArticle'),
                (SELECT COUNT(*) FROM Inspections WHERE json_extract(Document,'$.batchId')=$batch AND json_extract(Document,'$.purpose')='Production'),
                (SELECT COUNT(*) FROM Inspections WHERE json_extract(Document,'$.batchId')=$batch AND json_extract(Document,'$.purpose')='Reinspection'),
                (SELECT COUNT(*) FROM UploadState),
                EXISTS(SELECT 1 FROM Inspections WHERE json_extract(Document,'$.executionStatus')='Started'),
                EXISTS(SELECT 1 FROM PlcTriggers WHERE AckAt IS NULL);
            """;
        command.Parameters.AddWithValue("$batch", batch is null ? DBNull.Value : batch.Batch.Id.ToString());
        StationRuntimeUpdate result;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            result = new StationRuntimeUpdate(batch?.Batch.Id, batch?.ArchiveId,
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetBoolean(4), reader.GetBoolean(5), state, alarm, DateTimeOffset.UtcNow);
        }
        transaction.Commit();
        return result;
    }
}
