using System.Security.Claims;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Stations;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Batches;

public static class BatchClosureEndpoints
{
    public static void MapBatchClosure(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/batches/{id:guid}/closure", Check).RequireAuthorization("Human");
        app.MapPost("/api/batches/{id:guid}/close", Close).RequireAuthorization(policy => policy.RequireRole("QualityEngineer"));
    }

    private static async Task<IResult> Check(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(batch => batch.Id == id, token);
        return batch is null ? Results.NotFound() : Results.Ok(await ReadCheck(batch, db, token));
    }

    private static async Task<IResult> Close(Guid id, ClaimsPrincipal principal, UserManager<BoardTraceUser> users,
        BoardTraceDbContext db, CancellationToken token)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // This same batch-row write lock is held by review and first receipt writes.
        if (await db.Batches.Where(batch => batch.Id == id).ExecuteUpdateAsync(update => update.SetProperty(batch => batch.Status, batch => batch.Status), token) == 0)
            return Results.NotFound();
        var batch = await db.Batches.AsNoTracking().SingleAsync(batch => batch.Id == id, token);
        var prior = await db.BatchClosures.AsNoTracking().SingleOrDefaultAsync(closure => closure.BatchId == id, token);
        if (prior is not null) { await transaction.CommitAsync(token); return Results.Ok(prior); }
        var check = await ReadCheck(batch, db, token);
        if (!check.CanClose) return Results.Json(check, statusCode: 409);
        var audit = new BatchClosureAudit(id, batch.ArchiveId!.Value, user.Id, user.DisplayName, DateTimeOffset.UtcNow);
        db.BatchClosures.Add(audit);
        await db.Batches.Where(batch => batch.Id == id).ExecuteUpdateAsync(update => update.SetProperty(batch => batch.Status, BatchStatus.Closed), token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Results.Created($"/api/batches/{id}/closure", audit);
    }

    private static async Task<BatchClosureCheck> ReadCheck(BatchEntity batch, BoardTraceDbContext db, CancellationToken token)
    {
        var blockers = new List<string>();
        var counts = await db.Inspections.Where(row => row.BatchId == batch.Id).GroupBy(row => row.Purpose)
            .Select(group => new { Purpose = group.Key, Count = group.Count() }).ToDictionaryAsync(row => row.Purpose, row => row.Count, token);
        var central = new BatchCounts(counts.GetValueOrDefault(InspectionPurpose.FirstArticle),
            counts.GetValueOrDefault(InspectionPurpose.Production), counts.GetValueOrDefault(InspectionPurpose.Reinspection));
        if (batch.Status != BatchStatus.InProgress) blockers.Add(batch.Status == BatchStatus.Closed ? "批次已经关闭。" : "批次尚未处于生产状态。");
        if (!await db.FirstArticleApprovals.AnyAsync(approval => approval.BatchId == batch.Id, token)) blockers.Add("首件尚未批准。");
        if (central.Production != batch.PlannedQuantity) blockers.Add($"计划生产数量未全部收到：{central.Production}/{batch.PlannedQuantity}。");
        var unreviewed = await db.Inspections.CountAsync(row => row.BatchId == batch.Id &&
            (row.Purpose == InspectionPurpose.Reinspection || row.Purpose == InspectionPurpose.Production &&
                (row.Decision == QualityDecision.Fail || row.ExecutionStatus == InspectionExecution.Failed || row.ExecutionStatus == InspectionExecution.Interrupted)) &&
            !db.InspectionReviews.Any(review => review.InspectionId == row.Id), token);
        if (unreviewed != 0) blockers.Add($"仍有{unreviewed}条检测待质量复核。");
        var pendingOrders = await db.ReworkOrders.CountAsync(order => order.BatchId == batch.Id &&
            !db.Inspections.Any(row => row.ReworkOrderId == order.Id), token);
        if (pendingOrders != 0) blockers.Add($"仍有{pendingOrders}条返工指令未执行。");
        var saved = await db.StationRuntime.AsNoTracking().SingleOrDefaultAsync(row => row.StationId == batch.StationId, token);
        StationRuntimeView? station = null;
        if (saved is null) blockers.Add("尚无工位现场上报。");
        else
        {
            var runtime = saved.Read();
            var fresh = StationRuntimeEndpoints.IsFresh(saved.ReceivedAt, DateTimeOffset.UtcNow);
            station = new(batch.StationId, batch.StationId, runtime, saved.ReceivedAt, fresh,
                runtime.BatchId == batch.Id ? batch.BatchNumber : null);
            if (!fresh) blockers.Add("工位现场上报已超过30秒，请等待同步。");
            if (runtime.BatchId != batch.Id || batch.ArchiveId is null || runtime.ArchiveId != batch.ArchiveId)
                blockers.Add("工位当前批次或执行档案与中央绑定不一致。");
            if (runtime.FirstArticleCount != central.FirstArticle) blockers.Add($"首件现场/中央计数不一致：{runtime.FirstArticleCount}/{central.FirstArticle}。");
            if (runtime.ProductionCount != central.Production) blockers.Add($"生产现场/中央计数不一致：{runtime.ProductionCount}/{central.Production}。");
            if (runtime.ReinspectionCount != central.Reinspection) blockers.Add($"复检现场/中央计数不一致：{runtime.ReinspectionCount}/{central.Reinspection}。");
            if (runtime.PendingUploads != 0) blockers.Add($"工位仍有{runtime.PendingUploads}条待上传档案。");
            if (runtime.HasStartedInspection) blockers.Add("工位仍有尚未结束的检测。");
            if (runtime.HasUnacknowledgedPlc) blockers.Add("工位仍有未ACK的PLC结果。");
        }
        var closure = await db.BatchClosures.AsNoTracking().SingleOrDefaultAsync(value => value.BatchId == batch.Id, token);
        return new(batch.Id, batch.Status, blockers.Count == 0, blockers, central, station, unreviewed, pendingOrders, closure);
    }
}
