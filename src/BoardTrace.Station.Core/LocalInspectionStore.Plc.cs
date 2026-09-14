using System.Text.Json;
using BoardTrace.Contracts;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Core;

// Polling reads metadata only; image BLOBs remain available through Get(inspectionId).
public sealed record PlcInspectionState(InspectionRecord Record, DateTimeOffset? AckAt);

public sealed partial class LocalInspectionStore
{
    private static void InitializePlc(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PlcTriggers (
                ControllerSessionId TEXT NOT NULL,
                TriggerSequence INTEGER NOT NULL CHECK(TriggerSequence BETWEEN 1 AND 4294967295),
                InspectionId TEXT NOT NULL UNIQUE REFERENCES Inspections(Id),
                AckAt INTEGER,
                PRIMARY KEY(ControllerSessionId,TriggerSequence)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PlcTriggers_Unacknowledged ON PlcTriggers((1)) WHERE AckAt IS NULL;
            """;
        command.ExecuteNonQuery();
    }

    private static InspectionIdentity? ReadIdentity(InspectionRecord record)
    {
        if (record.ControllerSessionId is null && record.TriggerSequence is null) return null;
        if (record.ControllerSessionId is not Guid session || session == Guid.Empty || record.TriggerSequence is not uint sequence || sequence == 0)
            throw new InspectionRejectedException("PLC 触发身份必须同时提供非零会话和大于零的序号。");
        return new InspectionIdentity(session, sequence);
    }

    public PlcInspectionState? ReadPlcTrigger(InspectionIdentity identity)
    {
        using var connection = Open();
        return ReadPlcTrigger(connection, null, identity);
    }

    private static PlcInspectionState? ReadPlcTrigger(SqliteConnection connection, SqliteTransaction? transaction, InspectionIdentity identity)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.Document,p.AckAt FROM PlcTriggers p JOIN Inspections i ON i.Id=p.InspectionId
            WHERE p.ControllerSessionId=$controller AND p.TriggerSequence=$sequence;
            """;
        command.Parameters.AddWithValue("$controller", identity.ControllerSessionId.ToString());
        command.Parameters.AddWithValue("$sequence", (long)identity.TriggerSequence);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPlcState(reader) : null;
    }

    public PlcInspectionState? ReadUnacknowledgedPlc()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT i.Document,p.AckAt FROM PlcTriggers p JOIN Inspections i ON i.Id=p.InspectionId WHERE p.AckAt IS NULL;";
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPlcState(reader) : null;
    }

    private static PlcInspectionState ReadPlcState(SqliteDataReader reader) => new(
        JsonSerializer.Deserialize<InspectionRecord>(reader.GetString(0), Json)!,
        reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)));

    private static void RequireNoUnacknowledgedPlc(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM PlcTriggers WHERE AckAt IS NULL);";
        if ((long)command.ExecuteScalar()! != 0)
            throw new InspectionRejectedException("PLC 尚未确认上一检测结果，不能接新件或切换批次、方案。");
    }

    public void ConfirmPlcAck(Guid inspectionId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE PlcTriggers SET AckAt=COALESCE(AckAt,$now) WHERE InspectionId=$id
                AND EXISTS(SELECT 1 FROM Inspections i WHERE i.Id=$id
                    AND json_extract(i.Document,'$.executionStatus') IN ('Completed','Failed','Interrupted'));
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", inspectionId.ToString());
        if (command.ExecuteNonQuery() != 1)
            throw new InspectionRejectedException("PLC ACK 必须对应已结束的物理触发检测。");
        transaction.Commit();
    }

    // Called explicitly before accepting input on startup, never from ordinary Initialize.
    public int RecoverInterrupted()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT Document FROM Inspections WHERE json_extract(Document,'$.executionStatus')='Started';";
        var started = new List<InspectionRecord>();
        using (var reader = read.ExecuteReader())
            while (reader.Read()) started.Add(JsonSerializer.Deserialize<InspectionRecord>(reader.GetString(0), Json)!);
        foreach (var record in started)
        {
            var interrupted = record with { ExecutionStatus = InspectionExecution.Interrupted, Decision = QualityDecision.NotEvaluated,
                CompletedAt = DateTimeOffset.UtcNow, Error = "工位重启中断检测，未重放采图或物理动作。",
                Width = 0, Height = 0, Defects = [], Diagnostics = new Dictionary<string, double>(), DetectionMs = null };
            using var recover = connection.CreateCommand();
            recover.Transaction = transaction;
            recover.CommandText = """
                UPDATE Inspections SET Document=$document,TestedImage=NULL,ReferenceImage=NULL WHERE Id=$id;
                INSERT INTO UploadState(InspectionId) VALUES($id);
                """;
            recover.Parameters.AddWithValue("$id", record.Id.ToString());
            recover.Parameters.AddWithValue("$document", Document(interrupted));
            recover.ExecuteNonQuery();
        }
        transaction.Commit();
        return started.Count;
    }
}
