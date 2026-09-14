using System.Text.Json;
using BoardTrace.Contracts;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Core;

public sealed record CachedReworkOrder(ReworkOrder Order, Guid ArchiveId, Guid? InspectionId);

public sealed partial class LocalInspectionStore
{
    private static void InitializeRework(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ReworkOrders (
                Id TEXT PRIMARY KEY,
                BatchId TEXT NOT NULL REFERENCES CachedBatches(Id),
                ArchiveId TEXT NOT NULL REFERENCES BatchArchives(ArchiveId),
                Document TEXT NOT NULL,
                InspectionId TEXT UNIQUE REFERENCES Inspections(Id)
            );
            CREATE TABLE IF NOT EXISTS ActiveRework (
                Slot INTEGER PRIMARY KEY CHECK(Slot=1) REFERENCES ActiveBatch(Slot) ON DELETE CASCADE,
                OrderId TEXT NOT NULL REFERENCES ReworkOrders(Id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Inspections_ReworkOrder ON Inspections(json_extract(Document,'$.reworkOrderId'))
                WHERE json_extract(Document,'$.reworkOrderId') IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }

    // Called with an online personnel response; a refresh never resets local consumption.
    public void CacheReworkOrders(Guid batchId, IReadOnlyList<ReworkOrder> orders)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var active = ReadActiveBatch(connection, transaction) ?? throw new InspectionRejectedException("尚未加载批次。");
        if (active.Batch.Id != batchId || active.Status == BatchStatus.Closed)
            throw new InspectionRejectedException("返工指令不属于当前未关闭批次。");
        foreach (var order in orders)
        {
            RequireReworkBatch(order, active);
            var previous = ReadReworkOrder(connection, transaction, order.Id);
            if (previous is not null)
            {
                if (previous.Order != order || previous.ArchiveId != active.ArchiveId)
                    throw new InspectionRejectedException("返工指令内容或原工位档案已改变，不能覆盖缓存。");
                continue;
            }
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO ReworkOrders(Id,BatchId,ArchiveId,Document) VALUES($id,$batch,$archive,$document);";
            insert.Parameters.AddWithValue("$id", order.Id.ToString());
            insert.Parameters.AddWithValue("$batch", batchId.ToString());
            insert.Parameters.AddWithValue("$archive", active.ArchiveId.ToString());
            insert.Parameters.AddWithValue("$document", JsonSerializer.Serialize(order, Json));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<ReworkOrder> ReadPendingReworkOrders(Guid batchId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.Document FROM ReworkOrders o JOIN BatchArchives a ON a.BatchId=o.BatchId AND a.ArchiveId=o.ArchiveId
            WHERE o.BatchId=$batch AND o.InspectionId IS NULL ORDER BY json_extract(o.Document,'$.createdAt'),o.rowid;
            """;
        command.Parameters.AddWithValue("$batch", batchId.ToString());
        using var reader = command.ExecuteReader();
        var result = new List<ReworkOrder>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<ReworkOrder>(reader.GetString(0), Json)!);
        return result;
    }

    public CachedReworkOrder? ReadSelectedReworkOrder()
    {
        using var connection = Open();
        return ReadSelectedReworkOrder(connection, null);
    }

    private static CachedReworkOrder? ReadSelectedReworkOrder(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT o.Document,o.ArchiveId,o.InspectionId FROM ActiveRework r
            JOIN ReworkOrders o ON o.Id=r.OrderId JOIN ActiveBatch a ON a.Slot=r.Slot AND a.BatchId=o.BatchId WHERE r.Slot=1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCachedRework(reader) : null;
    }

    private static CachedReworkOrder? ReadReworkOrder(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Document,ArchiveId,InspectionId FROM ReworkOrders WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCachedRework(reader) : null;
    }

    private static CachedReworkOrder ReadCachedRework(SqliteDataReader reader) => new(
        JsonSerializer.Deserialize<ReworkOrder>(reader.GetString(0), Json)!, Guid.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)));

    private static void RequireReworkBatch(ReworkOrder order, CachedBatchState active)
    {
        if (order.BatchId != active.Batch.Id || order.StationId != active.Batch.StationId ||
            order.RecipeVersionId != active.Batch.RecipeVersionId || order.RecipeBundleHash != active.Batch.RecipeBundleHash)
            throw new InspectionRejectedException("返工指令与当前批次、工位或固定方案不一致。");
    }

    public void SelectReworkOrder(Guid orderId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireNoUnacknowledgedPlc(connection, transaction);
        var active = ReadActiveBatch(connection, transaction) ?? throw new InspectionRejectedException("尚未加载批次。");
        if (active.Status is not (BatchStatus.Approved or BatchStatus.InProgress))
            throw new InspectionRejectedException("复检需要首件已批准且未关闭的原批次。");
        var cached = ReadReworkOrder(connection, transaction, orderId) ?? throw new InspectionRejectedException("返工指令尚未在线获取。");
        RequireReworkBatch(cached.Order, active);
        if (cached.ArchiveId != active.ArchiveId || cached.InspectionId is not null)
            throw new InspectionRejectedException("返工指令已接件或不属于原本地档案，不能再次执行。");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ActiveRework(Slot,OrderId) VALUES(1,$id) ON CONFLICT(Slot) DO UPDATE SET OrderId=excluded.OrderId;";
        command.Parameters.AddWithValue("$id", orderId.ToString());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void ExitReinspection()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireNoUnacknowledgedPlc(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ActiveRework WHERE Slot=1;";
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
