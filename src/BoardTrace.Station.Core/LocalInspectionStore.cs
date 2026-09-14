using BoardTrace.Contracts;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Core;

public sealed record StoredInspection(InspectionRecord Record, bool PendingUpload, DateTimeOffset? AcknowledgedAt);

public sealed partial class LocalInspectionStore(string databasePath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string DatabasePath { get; } = Path.GetFullPath(databasePath);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Inspections (
                Id TEXT PRIMARY KEY,
                StartedAt INTEGER NOT NULL,
                Document TEXT NOT NULL,
                TestedImage BLOB,
                ReferenceImage BLOB
            );
            CREATE INDEX IF NOT EXISTS IX_Inspections_StartedAt ON Inspections(StartedAt DESC);
            CREATE TABLE IF NOT EXISTS UploadState (
                InspectionId TEXT PRIMARY KEY REFERENCES Inspections(Id)
            );
            CREATE TABLE IF NOT EXISTS UploadReceipts (
                InspectionId TEXT PRIMARY KEY REFERENCES Inspections(Id),
                ContentHash TEXT NOT NULL,
                ReceivedAt INTEGER NOT NULL,
                AcknowledgedAt INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        InitializeBatches(connection);
        InitializePlc(connection);
        InitializeImageRetention(connection);
        InitializeRework(connection);
    }

    // Images live only in their BLOB columns, never duplicated as base64 in the document.
    private static string Document(InspectionRecord record) =>
        JsonSerializer.Serialize(record with { TestedImage = null, ReferenceImage = null }, Json);

    public void Complete(InspectionRecord record)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Inspections SET Document=$document, TestedImage=$tested, ReferenceImage=$reference
            WHERE Id=$id AND json_extract(Document, '$.executionStatus')='Started'
                AND json_extract(Document, '$.controllerSessionId') IS $controller
                AND json_extract(Document, '$.triggerSequence') IS $sequence
                AND json_extract(Document, '$.reworkOrderId') IS $rework;
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$document", Document(record));
        command.Parameters.AddWithValue("$controller", record.ControllerSessionId is Guid controller ? controller.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$sequence", record.TriggerSequence is uint sequence ? (long)sequence : DBNull.Value);
        command.Parameters.AddWithValue("$rework", record.ReworkOrderId is Guid rework ? rework.ToString() : DBNull.Value);
        command.Parameters.Add("$tested", SqliteType.Blob).Value = (object?)record.TestedImage ?? DBNull.Value;
        command.Parameters.Add("$reference", SqliteType.Blob).Value = (object?)record.ReferenceImage ?? DBNull.Value;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("检测不存在或已结束，不能覆盖历史记录。");

        using var outbox = connection.CreateCommand();
        outbox.Transaction = transaction;
        outbox.CommandText = "INSERT INTO UploadState(InspectionId) VALUES ($id);";
        outbox.Parameters.AddWithValue("$id", record.Id.ToString());
        outbox.ExecuteNonQuery();
        transaction.Commit();
    }

    public InspectionRecord? Get(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.Document,i.TestedImage,i.ReferenceImage,p.PurgedAt FROM Inspections i
            LEFT JOIN LocalImagePurges p ON p.InspectionId=i.Id WHERE i.Id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadRecord(reader);
    }

    private static InspectionRecord ReadRecord(SqliteDataReader reader)
    {
        if (!reader.IsDBNull(3))
            throw new InvalidOperationException("本地图像已按保留期清理，请通过中央追溯查看完整档案。");
        return ReadImageRecord(reader);
    }

    private static InspectionRecord ReadImageRecord(SqliteDataReader reader)
    {
        var record = JsonSerializer.Deserialize<InspectionRecord>(reader.GetString(0), Json)!;
        return record with
        {
            TestedImage = reader.IsDBNull(1) ? null : (byte[])reader[1],
            ReferenceImage = reader.IsDBNull(2) ? null : (byte[])reader[2]
        };
    }

    public IReadOnlyList<StoredInspection> ReadRecent(int limit = 20)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.Document, u.InspectionId IS NOT NULL, r.AcknowledgedAt FROM Inspections i
            LEFT JOIN UploadState u ON i.Id=u.InspectionId
            LEFT JOIN UploadReceipts r ON i.Id=r.InspectionId
            ORDER BY i.StartedAt DESC, i.rowid DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var records = new List<StoredInspection>();
        while (reader.Read())
            records.Add(new StoredInspection(JsonSerializer.Deserialize<InspectionRecord>(reader.GetString(0), Json)!,
                reader.GetBoolean(1), reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        return records;
    }

    public IReadOnlyList<InspectionRecord> ReadPending(int limit = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.Document, i.TestedImage, i.ReferenceImage, p.PurgedAt FROM Inspections i
            JOIN UploadState u ON i.Id=u.InspectionId
            LEFT JOIN LocalImagePurges p ON p.InspectionId=i.Id
            ORDER BY i.StartedAt, i.rowid LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var records = new List<InspectionRecord>();
        while (reader.Read()) records.Add(ReadRecord(reader));
        return records;
    }

    public void ConfirmUploaded(InspectionReceipt receipt)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT i.Document, i.TestedImage, i.ReferenceImage, p.PurgedAt FROM Inspections i
            JOIN UploadState u ON i.Id=u.InspectionId
            LEFT JOIN LocalImagePurges p ON p.InspectionId=i.Id WHERE i.Id=$id;
            """;
        read.Parameters.AddWithValue("$id", receipt.InspectionId.ToString());
        InspectionRecord record;
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("中央回执未对应待上传记录。");
            record = ReadRecord(reader);
        }
        if (!string.Equals(receipt.ContentHash, InspectionTransfer.Hash(record), StringComparison.Ordinal))
            throw new InvalidDataException("中央回执内容与本地档案不符，保留待上传记录。");

        using var confirm = connection.CreateCommand();
        confirm.Transaction = transaction;
        confirm.CommandText = """
            INSERT INTO UploadReceipts(InspectionId, ContentHash, ReceivedAt, AcknowledgedAt)
            VALUES ($id, $hash, $received, $acknowledged);
            DELETE FROM UploadState WHERE InspectionId=$id;
            """;
        confirm.Parameters.AddWithValue("$id", receipt.InspectionId.ToString());
        confirm.Parameters.AddWithValue("$hash", receipt.ContentHash);
        confirm.Parameters.AddWithValue("$received", receipt.ReceivedAt.ToUnixTimeMilliseconds());
        confirm.Parameters.AddWithValue("$acknowledged", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        confirm.ExecuteNonQuery();
        transaction.Commit();
    }

    public int PendingCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM UploadState;";
        return checked((int)(long)command.ExecuteScalar()!);
    }
}
