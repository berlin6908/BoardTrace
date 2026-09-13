using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Batches;

public static class BatchInspectionGate
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<string?> Check(InspectionRecord record, BoardTraceDbContext db, CancellationToken token)
    {
        if (record.Purpose == InspectionPurpose.EngineeringReplay)
            return record.BatchId is null && record.ExecutionSessionId is null && record.ProductionSequence is null
                ? null : "工程回放不能携带批次、执行会话或生产序号。";
        if (record.BatchId is null) return "首件和生产检测必须绑定批次。";
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(batch => batch.Id == record.BatchId, token);
        if (batch is null || batch.StationId != record.StationId) return "批次不存在或不属于该工位。";
        if (record.RecipeId != batch.RecipeVersionId.ToString("D")) return "检测版本与批次固定版本不符。";
        PublishedRecipeVersion? snapshot;
        try { snapshot = JsonSerializer.Deserialize<PublishedRecipeVersion>(record.RecipeJson, Json); }
        catch (JsonException) { return "检测必须携带完整发布方案包快照。"; }
        if (snapshot?.Bundle is null || snapshot.Bundle.References is null || snapshot.Bundle.VersionId != batch.RecipeVersionId ||
            snapshot.BundleHash != batch.RecipeBundleHash || PublishedRecipeTransfer.Hash(snapshot.Bundle) != batch.RecipeBundleHash)
            return "检测方案包与批次固定包不符。";
        var reference = snapshot.Bundle.References.SingleOrDefault(reference => reference.SampleId == record.SampleId);
        if (reference is null || record.SourceKind is not ("Replay" or "ConstructedNormal")) return "检测样本或模拟来源不属于允许的固定输入。";
        if (record.ReferenceImage is not null && (record.ReferenceImage.Length != reference.ByteLength ||
            Convert.ToHexStringLower(SHA256.HashData(record.ReferenceImage)) != reference.Sha256)) return "检测参考图与已发布资产不符。";
        if (record.Purpose == InspectionPurpose.FirstArticle)
        {
            if (record.ExecutionSessionId is not null || record.ProductionSequence is not null) return "首件不能消耗生产序号或使用生产会话。";
            var approval = await db.FirstArticleApprovals.AsNoTracking().SingleOrDefaultAsync(approval => approval.BatchId == batch.Id, token);
            if (approval is null && batch.Status != BatchStatus.AwaitingFirstArticle) return "批次当前状态不允许首次接收首件。";
            return approval is not null && record.StartedAt > approval.ApprovedAt ? "首件批准后不能再接受新的首件。" : null;
        }
        if (batch.Status is not (BatchStatus.InProgress or BatchStatus.Closed)) return "批次尚未启动生产。";
        if (record.ProductionSequence is not int sequence || sequence < 1 || sequence > batch.PlannedQuantity) return "生产序号超出本批计划数量。";
        var session = await db.BatchExecutionSessions.AsNoTracking().SingleOrDefaultAsync(session => session.Id == record.ExecutionSessionId, token);
        if (session is null || session.BatchId != batch.Id || session.StationId != record.StationId || session.RecipeBundleHash != batch.RecipeBundleHash ||
            session.OperatorId != record.OperatorId || session.OperatorName != record.OperatorName || record.StartedAt < session.IssuedAt || record.StartedAt >= session.ExpiresAt)
            return "检测接受时间、人员或批次不符合历史执行会话。";
        return null;
    }
}
