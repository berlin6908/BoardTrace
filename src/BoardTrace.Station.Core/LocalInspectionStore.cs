using BoardTrace.Contracts;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Core;

public sealed record StoredInspection(InspectionRecord Record, bool PendingUpload);

public sealed class LocalInspectionStore(string databasePath)
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
            """;
        command.ExecuteNonQuery();
    }

    // Images live only in their BLOB columns, never duplicated as base64 in the document.
    private static string Document(InspectionRecord record) =>
        JsonSerializer.Serialize(record with { TestedImage = null, ReferenceImage = null }, Json);

    public void Begin(InspectionRecord record)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Inspections(Id, StartedAt, Document) VALUES ($id, $started, $document);";
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$started", record.StartedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$document", Document(record));
        command.ExecuteNonQuery();
    }

    public void Complete(InspectionRecord record)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Inspections SET Document=$document, TestedImage=$tested, ReferenceImage=$reference
            WHERE Id=$id AND json_extract(Document, '$.executionStatus')='Started';
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$document", Document(record));
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
        command.CommandText = "SELECT Document, TestedImage, ReferenceImage FROM Inspections WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
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
            SELECT i.Document, u.InspectionId IS NOT NULL FROM Inspections i
            LEFT JOIN UploadState u ON i.Id=u.InspectionId
            ORDER BY i.StartedAt DESC, i.rowid DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var records = new List<StoredInspection>();
        while (reader.Read())
            records.Add(new StoredInspection(JsonSerializer.Deserialize<InspectionRecord>(reader.GetString(0), Json)!, reader.GetBoolean(1)));
        return records;
    }

    public int PendingCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM UploadState;";
        return checked((int)(long)command.ExecuteScalar()!);
    }
}
