using System.Text.Json;
using BoardTrace.Contracts;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Core;

public sealed class InspectionRejectedException(string message) : InvalidOperationException(message);

public sealed record CachedBatchState(BatchDefinition Batch, BatchStatus Status, FirstArticleApproval? Approval,
    int NextProductionSequence, BatchExecutionSession? Session, Guid ArchiveId)
{
    public int AcceptedProductionCount => NextProductionSequence - 1;
}

public sealed record InspectionAcceptance(InspectionRecord Record, bool IsNew);
public sealed record CachedBatchResume(CachedBatchState Batch, byte[] ProtectedPayload, bool HasPendingRework);

public sealed partial class LocalInspectionStore
{
    private static void InitializeBatches(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CachedBatches (
                Id TEXT PRIMARY KEY,
                RecipeVersionId TEXT NOT NULL REFERENCES Recipes(VersionId),
                RecipeBundleHash TEXT NOT NULL,
                Document TEXT NOT NULL,
                Status TEXT NOT NULL,
                Approval TEXT,
                NextProductionSequence INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS ActiveBatch (
                Slot INTEGER PRIMARY KEY CHECK(Slot=1),
                BatchId TEXT NOT NULL REFERENCES CachedBatches(Id),
                ExecutionSession TEXT
            );
            CREATE TABLE IF NOT EXISTS BatchArchives (
                BatchId TEXT PRIMARY KEY REFERENCES CachedBatches(Id),
                ArchiveId TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS OfflineBatchResume (
                Slot INTEGER PRIMARY KEY CHECK(Slot=1) REFERENCES ActiveBatch(Slot) ON DELETE CASCADE,
                SessionId TEXT NOT NULL,
                ProtectedPayload BLOB NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Inspections_BatchSequence ON Inspections(
                json_extract(Document, '$.batchId'), json_extract(Document, '$.productionSequence'))
                WHERE json_extract(Document, '$.purpose')='Production';
            """;
        command.ExecuteNonQuery();
    }

    public CachedBatchState? ReadActiveBatch()
    {
        using var connection = Open();
        return ReadActiveBatch(connection, null);
    }

    public CachedBatchResume? ReadBatchResume()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var active = ReadActiveBatch(connection, transaction);
        if (active?.Session is not { } session) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ProtectedPayload FROM OfflineBatchResume WHERE Slot=1 AND SessionId=$session;";
        command.Parameters.AddWithValue("$session", session.Id.ToString());
        var payload = command.ExecuteScalar() as byte[];
        using var pending = connection.CreateCommand();
        pending.Transaction = transaction;
        pending.CommandText = "SELECT EXISTS(SELECT 1 FROM ReworkOrders WHERE BatchId=$batch AND ArchiveId=$archive AND InspectionId IS NULL);";
        pending.Parameters.AddWithValue("$batch", active.Batch.Id.ToString());
        pending.Parameters.AddWithValue("$archive", active.ArchiveId.ToString());
        var hasPendingRework = (long)pending.ExecuteScalar()! != 0;
        transaction.Commit();
        return payload is null ? null : new CachedBatchResume(active, payload, hasPendingRework);
    }

    public StoredInspection? ReadPassedFirstArticle(Guid batchId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.Document,u.InspectionId IS NOT NULL,r.AcknowledgedAt FROM Inspections i
            LEFT JOIN UploadState u ON u.InspectionId=i.Id
            LEFT JOIN UploadReceipts r ON r.InspectionId=i.Id
            WHERE json_extract(i.Document,'$.batchId')=$batch
                AND json_extract(i.Document,'$.purpose')='FirstArticle'
                AND json_extract(i.Document,'$.executionStatus')='Completed'
                AND json_extract(i.Document,'$.decision')='Pass'
            ORDER BY i.StartedAt,i.rowid LIMIT 1;
            """;
        command.Parameters.AddWithValue("$batch", batchId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new StoredInspection(JsonSerializer.Deserialize<InspectionRecord>(reader.GetString(0), Json)!,
            reader.GetBoolean(1), reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))) : null;
    }

    private static CachedBatchState? ReadActiveBatch(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT b.Document,b.Status,b.Approval,b.NextProductionSequence,a.ExecutionSession,r.ArchiveId
            FROM ActiveBatch a JOIN CachedBatches b ON b.Id=a.BatchId
            LEFT JOIN BatchArchives r ON r.BatchId=b.Id WHERE a.Slot=1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.IsDBNull(5)) throw new InvalidDataException("本地批次缺少执行档案身份，请恢复对应版本的原工位 SQLite 档案。");
        return new CachedBatchState(
            JsonSerializer.Deserialize<BatchDefinition>(reader.GetString(0), Json)!,
            Enum.Parse<BatchStatus>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<FirstArticleApproval>(reader.GetString(2), Json),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<BatchExecutionSession>(reader.GetString(4), Json),
            Guid.Parse(reader.GetString(5)));
    }

    // Call only with the complete recipe already loaded and verified by LocalRecipeStore.
    public void SelectBatch(BatchPackage package, LoadedRecipe recipe)
    {
        var batch = package.Batch;
        if (recipe.VersionId != batch.RecipeVersionId || recipe.BundleHash != batch.RecipeBundleHash ||
            package.Recipe.Bundle.VersionId != recipe.VersionId || package.Recipe.BundleHash != recipe.BundleHash)
            throw new InspectionRejectedException("批次与完整缓存方案版本不一致。");
        if (batch.PlannedQuantity <= 0)
            throw new InspectionRejectedException("批次计划数量无效，不能下发。");
        if (package.Approval is { } approval && approval.BatchId != batch.Id)
            throw new InspectionRejectedException("首件批准不属于该批次。");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireNoUnacknowledgedPlc(connection, transaction);
        var active = ReadActiveBatch(connection, transaction);
        if (package.Status == BatchStatus.Closed && active?.Batch.Id != batch.Id)
            throw new InspectionRejectedException("已关闭批次只能刷新当前本地批次，不能重新下发。");
        if (active is not null && active.Batch.Id != batch.Id && active.Status != BatchStatus.Closed)
            throw new InspectionRejectedException("当前工位已有未关闭批次，不能切换。");
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT b.Document,b.Status,b.Approval,r.ArchiveId FROM CachedBatches b LEFT JOIN BatchArchives r ON r.BatchId=b.Id WHERE b.Id=$id;";
        existing.Parameters.AddWithValue("$id", batch.Id.ToString());
        BatchStatus? previousStatus = null;
        FirstArticleApproval? previousApproval = null;
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.IsDBNull(3)) throw new InvalidDataException("本地批次缺少执行档案身份，请恢复对应版本的原工位 SQLite 档案。");
                if (JsonSerializer.Deserialize<BatchDefinition>(reader.GetString(0), Json) != batch)
                    throw new InspectionRejectedException("批次的产品、数量、工位或方案已改变，不能替换本地批次。");
                previousStatus = Enum.Parse<BatchStatus>(reader.GetString(1));
                previousApproval = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<FirstArticleApproval>(reader.GetString(2), Json);
                if (previousApproval is not null && package.Approval is not null && previousApproval != package.Approval)
                    throw new InspectionRejectedException("已缓存的首件批准不能被另一记录覆盖。");
            }
        }
        var status = previousStatus is not null && previousStatus > package.Status ? previousStatus.Value : package.Status;
        var savedApproval = package.Approval ?? previousApproval;
        if (status is BatchStatus.Approved or BatchStatus.InProgress && savedApproval is null)
            throw new InspectionRejectedException("批次缺少首件批准，不能启用。");
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            INSERT INTO CachedBatches(Id,RecipeVersionId,RecipeBundleHash,Document,Status,Approval)
            SELECT $id,$version,$hash,$document,$status,$approval
            WHERE EXISTS(SELECT 1 FROM Recipes WHERE VersionId=$version AND BundleHash=$hash)
            ON CONFLICT(Id) DO UPDATE SET Status=excluded.Status,Approval=excluded.Approval;
            """;
        write.Parameters.AddWithValue("$id", batch.Id.ToString());
        write.Parameters.AddWithValue("$version", batch.RecipeVersionId.ToString());
        write.Parameters.AddWithValue("$hash", batch.RecipeBundleHash);
        write.Parameters.AddWithValue("$document", JsonSerializer.Serialize(batch, Json));
        write.Parameters.AddWithValue("$status", status.ToString());
        write.Parameters.AddWithValue("$approval", savedApproval is null ? DBNull.Value : JsonSerializer.Serialize(savedApproval, Json));
        if (write.ExecuteNonQuery() != 1) throw new InspectionRejectedException("完整方案尚未缓存，不能启用批次。");
        if (previousStatus is null)
        {
            using var archive = connection.CreateCommand();
            archive.Transaction = transaction;
            archive.CommandText = "INSERT INTO BatchArchives(BatchId,ArchiveId) VALUES($batch,$archive);";
            archive.Parameters.AddWithValue("$batch", batch.Id.ToString());
            archive.Parameters.AddWithValue("$archive", Guid.NewGuid().ToString());
            archive.ExecuteNonQuery();
        }
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            INSERT INTO ActiveBatch(Slot,BatchId) VALUES (1,$id)
            ON CONFLICT(Slot) DO UPDATE SET BatchId=excluded.BatchId,
                ExecutionSession=CASE WHEN $closed=0 AND ActiveBatch.BatchId=excluded.BatchId THEN ActiveBatch.ExecutionSession ELSE NULL END;
            """;
        select.Parameters.AddWithValue("$id", batch.Id.ToString());
        select.Parameters.AddWithValue("$closed", status == BatchStatus.Closed);
        select.ExecuteNonQuery();
        if (active?.Batch.Id != batch.Id || status == BatchStatus.Closed)
        {
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM OfflineBatchResume WHERE Slot=1;";
            if (active?.Batch.Id != batch.Id) clear.CommandText += " DELETE FROM ActiveRework WHERE Slot=1;";
            clear.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SaveExecutionSession(BatchExecutionSession session, byte[]? resumePayload)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireNoUnacknowledgedPlc(connection, transaction);
        var active = ReadActiveBatch(connection, transaction) ?? throw new InspectionRejectedException("尚未加载批次。");
        if (active.ArchiveId != session.ArchiveId || active.Batch.Id != session.BatchId || active.Batch.StationId != session.StationId ||
            active.Batch.RecipeBundleHash != session.RecipeBundleHash || active.Approval?.InspectionId != session.FirstArticleInspectionId ||
            active.Status is not (BatchStatus.Approved or BatchStatus.InProgress))
            throw new InspectionRejectedException("启动会话与批次、方案或首件批准不一致。");
        if (session.ExpiresAt <= DateTimeOffset.UtcNow || session.IssuedAt >= session.ExpiresAt)
            throw new InspectionRejectedException("人员启动会话已过期，请重新登录并在线启动。");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ActiveBatch SET ExecutionSession=$session WHERE Slot=1;
            UPDATE CachedBatches SET Status='InProgress' WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$session", JsonSerializer.Serialize(session, Json));
        command.Parameters.AddWithValue("$id", active.Batch.Id.ToString());
        command.ExecuteNonQuery();
        using var resume = connection.CreateCommand();
        resume.Transaction = transaction;
        if (resumePayload is null)
        {
            resume.CommandText = "DELETE FROM OfflineBatchResume WHERE Slot=1;";
        }
        else
        {
            resume.CommandText = """
                INSERT INTO OfflineBatchResume(Slot,SessionId,ProtectedPayload) VALUES(1,$session,$payload)
                ON CONFLICT(Slot) DO UPDATE SET SessionId=excluded.SessionId,ProtectedPayload=excluded.ProtectedPayload;
                """;
            resume.Parameters.AddWithValue("$session", session.Id.ToString());
            resume.Parameters.Add("$payload", SqliteType.Blob).Value = resumePayload;
        }
        resume.ExecuteNonQuery();
        transaction.Commit();
    }

    public void ClearExecutionSession()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ActiveBatch SET ExecutionSession=NULL WHERE Slot=1;
            DELETE FROM OfflineBatchResume WHERE Slot=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void LeaveBatch()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireNoUnacknowledgedPlc(connection, transaction);
        var active = ReadActiveBatch(connection, transaction);
        if (active?.Status == BatchStatus.InProgress)
            throw new InspectionRejectedException("在制批次不能切换为工程回放，请先完成批次。");
        if (ReadSelectedReworkOrder(connection, transaction) is not null)
            throw new InspectionRejectedException("请先明确退出复检模式，再切换工程方案。");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ActiveBatch WHERE Slot=1;";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public InspectionAcceptance BeginAccepted(InspectionRecord record, string? loadedBundleHash)
    {
        if (record.ExecutionStatus != InspectionExecution.Started)
            throw new InspectionRejectedException("接件只能创建 Started 记录。");
        var identity = ReadIdentity(record);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (identity is { } key && ReadPlcTrigger(connection, transaction, key) is { } previous)
        {
            if (previous.Record.StationId != record.StationId || previous.Record.ProductId != record.ProductId || previous.Record.SampleId != record.SampleId)
                throw new InspectionRejectedException("同一 PLC 触发身份的工位、产品或样本已改变。");
            return new InspectionAcceptance(previous.Record, false);
        }
        RequireNoUnacknowledgedPlc(connection, transaction);
        var active = ReadActiveBatch(connection, transaction);
        var selectedRework = ReadSelectedReworkOrder(connection, transaction);
        if (record.Purpose != InspectionPurpose.Reinspection && (record.ReworkOrderId is not null || selectedRework is not null))
            throw new InspectionRejectedException("当前为复检模式，请明确退出后再接普通生产件。");
        if (record.Purpose == InspectionPurpose.EngineeringReplay)
        {
            if (active is not null) throw new InspectionRejectedException("请先退出当前批次选择，再执行工程回放。");
        }
        else
        {
            if (active is null) throw new InspectionRejectedException("尚未下载并加载完整批次。");
            var batch = active.Batch;
            if (batch.StationId != record.StationId || batch.RecipeVersionId.ToString() != record.RecipeId || batch.RecipeBundleHash != loadedBundleHash)
                throw new InspectionRejectedException("当前工位或已加载方案与批次不一致。");
            if (active.Status == BatchStatus.Closed) throw new InspectionRejectedException("批次已关闭，不能接件。");
            record = record with { BatchId = batch.Id };
            if (record.Purpose == InspectionPurpose.FirstArticle)
            {
                if (active.Status != BatchStatus.AwaitingFirstArticle || active.Approval is not null)
                    throw new InspectionRejectedException("批次已完成首件批准，不能再次执行首件。");
                using var pass = connection.CreateCommand();
                pass.Transaction = transaction;
                pass.CommandText = """
                    SELECT COUNT(*) FROM Inspections WHERE json_extract(Document,'$.batchId')=$batch
                        AND json_extract(Document,'$.purpose')='FirstArticle'
                        AND json_extract(Document,'$.executionStatus')='Completed' AND json_extract(Document,'$.decision')='Pass';
                    """;
                pass.Parameters.AddWithValue("$batch", batch.Id.ToString());
                if ((long)pass.ExecuteScalar()! != 0) throw new InspectionRejectedException("首件已通过，等待上传和质量批准。");
            }
            else if (record.Purpose is InspectionPurpose.Production or InspectionPurpose.Reinspection)
            {
                var session = active.Session;
                if (active.Status != BatchStatus.InProgress || active.Approval is null || session is null)
                    throw new InspectionRejectedException("批次须完成首件批准并在线启动后才能生产。");
                if (session.ArchiveId != active.ArchiveId || session.OperatorId != record.OperatorId || session.BatchId != batch.Id || session.StationId != record.StationId ||
                    session.RecipeBundleHash != loadedBundleHash || session.FirstArticleInspectionId != active.Approval.InspectionId)
                    throw new InspectionRejectedException("当前操作员或方案不属于已启动批次会话。");
                if (record.StartedAt < session.IssuedAt || record.StartedAt >= session.ExpiresAt)
                    throw new InspectionRejectedException("人员启动会话已过期，请重新登录并在线启动。");
                record = record with { ExecutionSessionId = session.Id, ProductionSequence = null };
                if (record.Purpose == InspectionPurpose.Reinspection)
                {
                    if (selectedRework is null || selectedRework.InspectionId is not null || selectedRework.Order.Id != record.ReworkOrderId)
                        throw new InspectionRejectedException("须选择尚未接件的缓存返工指令；已中断的复检不能重采。");
                    RequireReworkBatch(selectedRework.Order, active);
                    if (selectedRework.ArchiveId != active.ArchiveId || selectedRework.Order.ProductId != record.ProductId ||
                        selectedRework.Order.SampleId != record.SampleId)
                        throw new InspectionRejectedException("复检须使用原档案及指令固定的产品、样本。");
                }
                else
                {
                    if (active.NextProductionSequence > batch.PlannedQuantity)
                        throw new InspectionRejectedException("批次计划数量已用完，不能接受新生产件。");
                    record = record with { ProductionSequence = active.NextProductionSequence };
                    using var next = connection.CreateCommand();
                    next.Transaction = transaction;
                    next.CommandText = "UPDATE CachedBatches SET NextProductionSequence=NextProductionSequence+1 WHERE Id=$id;";
                    next.Parameters.AddWithValue("$id", batch.Id.ToString());
                    next.ExecuteNonQuery();
                }
            }
            else throw new InspectionRejectedException("检测用途无效。");
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO Inspections(Id,StartedAt,Document) VALUES($id,$started,$document);";
        insert.Parameters.AddWithValue("$id", record.Id.ToString());
        insert.Parameters.AddWithValue("$started", record.StartedAt.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$document", Document(record));
        insert.ExecuteNonQuery();
        if (record.Purpose == InspectionPurpose.Reinspection)
        {
            using var consume = connection.CreateCommand();
            consume.Transaction = transaction;
            consume.CommandText = "UPDATE ReworkOrders SET InspectionId=$inspection WHERE Id=$order AND InspectionId IS NULL;";
            consume.Parameters.AddWithValue("$inspection", record.Id.ToString());
            consume.Parameters.AddWithValue("$order", record.ReworkOrderId!.Value.ToString());
            if (consume.ExecuteNonQuery() != 1) throw new InspectionRejectedException("返工指令已接件，不能重复执行。");
        }
        if (identity is { } trigger)
        {
            using var physical = connection.CreateCommand();
            physical.Transaction = transaction;
            physical.CommandText = "INSERT INTO PlcTriggers(ControllerSessionId,TriggerSequence,InspectionId) VALUES($controller,$sequence,$id);";
            physical.Parameters.AddWithValue("$controller", trigger.ControllerSessionId.ToString());
            physical.Parameters.AddWithValue("$sequence", (long)trigger.TriggerSequence);
            physical.Parameters.AddWithValue("$id", record.Id.ToString());
            physical.ExecuteNonQuery();
        }
        transaction.Commit();
        return new InspectionAcceptance(record, true);
    }
}
