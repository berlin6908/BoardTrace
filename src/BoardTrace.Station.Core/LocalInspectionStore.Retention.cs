using BoardTrace.Contracts;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Core;

// After image cleanup, the receipt retains the hash of the original complete archive.
public sealed record LocalInspectionArchive(InspectionRecord Record, DateTimeOffset? ImagesPurgedAt, InspectionReceipt? Receipt);

public sealed partial class LocalInspectionStore
{
    private static void InitializeImageRetention(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS LocalImagePurges (
                InspectionId TEXT PRIMARY KEY REFERENCES Inspections(Id),
                PurgedAt INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public LocalInspectionArchive? ReadArchive(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.Document,i.TestedImage,i.ReferenceImage,p.PurgedAt,r.ContentHash,r.ReceivedAt
            FROM Inspections i
            LEFT JOIN LocalImagePurges p ON p.InspectionId=i.Id
            LEFT JOIN UploadReceipts r ON r.InspectionId=i.Id
            WHERE i.Id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(ReadImageRecord(reader), reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : new InspectionReceipt(id, reader.GetString(4), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
    }

    public int PurgeAcknowledgedImages(DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = """
            INSERT INTO LocalImagePurges(InspectionId,PurgedAt)
            SELECT i.Id,$now FROM Inspections i JOIN UploadReceipts r ON r.InspectionId=i.Id
            WHERE r.AcknowledgedAt<=$cutoff
                AND json_extract(i.Document,'$.executionStatus')<>'Started'
                AND (i.TestedImage IS NOT NULL OR i.ReferenceImage IS NOT NULL)
                AND NOT EXISTS(SELECT 1 FROM UploadState u WHERE u.InspectionId=i.Id)
                AND NOT EXISTS(SELECT 1 FROM PlcTriggers p WHERE p.InspectionId=i.Id AND p.AckAt IS NULL)
                AND NOT EXISTS(SELECT 1 FROM LocalImagePurges c WHERE c.InspectionId=i.Id)
            ORDER BY i.StartedAt,i.rowid
            LIMIT 100
            RETURNING InspectionId;
            """;
        mark.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        mark.Parameters.AddWithValue("$cutoff", now.AddDays(-7).ToUnixTimeMilliseconds());
        var ids = new List<string>();
        using (var reader = mark.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "UPDATE Inspections SET TestedImage=NULL,ReferenceImage=NULL WHERE Id=$id;";
        var id = clear.Parameters.Add("$id", SqliteType.Text);
        foreach (var inspectionId in ids)
        {
            id.Value = inspectionId;
            clear.ExecuteNonQuery();
        }
        transaction.Commit();
        return ids.Count;
    }
}
