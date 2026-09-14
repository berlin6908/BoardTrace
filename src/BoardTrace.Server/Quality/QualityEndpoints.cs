using System.Security.Claims;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Quality;

public static class QualityEndpoints
{
    public static void MapQuality(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/quality/queue", Queue).RequireAuthorization("Human");
        app.MapGet("/api/quality/inspections/{id:guid}", Details).RequireAuthorization("Human");
        app.MapPost("/api/quality/inspections/{id:guid}/review", Review).RequireAuthorization(policy => policy.RequireRole("QualityEngineer"));
        app.MapGet("/api/batches/{id:guid}/rework-orders", PendingOrders).RequireAuthorization("Human");
    }

    private static async Task<IResult> Queue(Guid? batchId, string? stationId, int? page, int? pageSize,
        BoardTraceDbContext db, CancellationToken token)
    {
        var number = page ?? 1;
        var size = pageSize ?? 20;
        if (number is < 1 or > 1_000_000 || size is < 1 or > 100)
            return Results.BadRequest("页码至少为1，每页1–100条。");
        var query = db.Inspections.AsNoTracking().Where(row =>
            (row.Purpose == InspectionPurpose.Reinspection ||
             row.Purpose == InspectionPurpose.Production && (row.Decision == QualityDecision.Fail ||
                 row.ExecutionStatus == InspectionExecution.Failed || row.ExecutionStatus == InspectionExecution.Interrupted)) &&
            !db.InspectionReviews.Any(review => review.InspectionId == row.Id));
        if (batchId is not null) query = query.Where(row => row.BatchId == batchId);
        if (!string.IsNullOrWhiteSpace(stationId)) query = query.Where(row => row.StationId == stationId);
        var total = await query.CountAsync(token);
        var items = await query.Join(db.Batches, row => row.BatchId, batch => batch.Id,
                (row, batch) => new { Inspection = row, batch.BatchNumber })
            .OrderBy(value => value.Inspection.StartedAt).ThenBy(value => value.Inspection.Id)
            .Skip((number - 1) * size).Take(size)
            .Select(value => new QualityQueueItem(value.Inspection.Id, value.Inspection.BatchId!.Value,
                value.BatchNumber, value.Inspection.StationId, value.Inspection.ProductId, value.Inspection.SampleId,
                value.Inspection.Purpose, value.Inspection.ExecutionStatus, value.Inspection.Decision, value.Inspection.StartedAt))
            .ToArrayAsync(token);
        return Results.Ok(new QualityQueuePage(items, total, number, size));
    }

    private static async Task<IResult> Details(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        var row = await db.Inspections.AsNoTracking().Where(row => row.Id == id)
            .Select(row => new { row.ReworkOrderId }).SingleOrDefaultAsync(token);
        return row is null ? Results.NotFound() : Results.Ok(await ReadDetails(id, row.ReworkOrderId, db, token));
    }

    private static async Task<InspectionQualityDetails> ReadDetails(Guid id, Guid? sourceOrderId,
        BoardTraceDbContext db, CancellationToken token)
    {
        var review = await db.InspectionReviews.AsNoTracking().SingleOrDefaultAsync(review => review.InspectionId == id, token);
        var source = sourceOrderId is null ? null : await db.ReworkOrders.AsNoTracking().SingleAsync(order => order.Id == sourceOrderId, token);
        var order = await db.ReworkOrders.AsNoTracking().SingleOrDefaultAsync(order => order.OriginalInspectionId == id, token);
        var reinspectionId = order is null ? null : await db.Inspections.Where(row => row.ReworkOrderId == order.Id).Select(row => (Guid?)row.Id).SingleOrDefaultAsync(token);
        return new(review, source, order, reinspectionId);
    }

    private static async Task<IResult> PendingOrders(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        if (!await db.Batches.AnyAsync(batch => batch.Id == id, token)) return Results.NotFound();
        return Results.Ok(await db.ReworkOrders.AsNoTracking().Where(order => order.BatchId == id &&
                !db.Inspections.Any(row => row.ReworkOrderId == order.Id))
            .OrderBy(order => order.CreatedAt).ThenBy(order => order.Id).ToArrayAsync(token));
    }

    private static async Task<IResult> Review(Guid id, CreateInspectionReviewRequest request, ClaimsPrincipal principal,
        UserManager<BoardTraceUser> users, BoardTraceDbContext db, CancellationToken token)
    {
        if (!Enum.IsDefined(request.Disposition) || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 2000)
            return Results.BadRequest("处置无效，复核意见必填且最多2000字符。");
        var user = await users.GetUserAsync(principal);
        if (user is null) return Results.Unauthorized();
        var row = await db.Inspections.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, token);
        if (row is null) return Results.NotFound();
        var existing = await db.InspectionReviews.AsNoTracking().SingleOrDefaultAsync(review => review.InspectionId == id, token);
        if (existing is not null) return await Repeat(existing, request, user.Id, row.ReworkOrderId, db, token);
        if (row.Purpose is not (InspectionPurpose.Production or InspectionPurpose.Reinspection))
            return Conflict("只复核生产和复检档案；首件使用首件批准流程。");
        var allowed = row.ExecutionStatus switch
        {
            InspectionExecution.Completed => request.Disposition is ReviewDisposition.Accept or ReviewDisposition.Reject or ReviewDisposition.Rework,
            InspectionExecution.Failed or InspectionExecution.Interrupted => request.Disposition is ReviewDisposition.ResolveTechnicalIssue or ReviewDisposition.Rework,
            _ => false
        };
        if (!allowed) return Conflict("该执行状态不允许此人工处置；技术失败或中断不能形成质量接受或拒收判定。");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Batches.Where(batch => batch.Id == row.BatchId).ExecuteUpdateAsync(update =>
            update.SetProperty(batch => batch.Status, batch => batch.Status), token);
        // Re-check after acquiring the same lock used by closure and receipt writes.
        existing = await db.InspectionReviews.AsNoTracking().SingleOrDefaultAsync(review => review.InspectionId == id, token);
        if (existing is not null)
        {
            await transaction.CommitAsync(token);
            return await Repeat(existing, request, user.Id, row.ReworkOrderId, db, token);
        }
        var batch = await db.Batches.AsNoTracking().SingleAsync(batch => batch.Id == row.BatchId, token);
        if (batch.Status == BatchStatus.Closed) return Conflict("批次已经关闭，不能新增复核或返工指令。");
        var now = DateTimeOffset.UtcNow;
        var review = new InspectionReview(id, request.Disposition, request.Note, user.Id, user.DisplayName, now);
        db.InspectionReviews.Add(review);
        if (request.Disposition == ReviewDisposition.Rework)
        {
            db.ReworkOrders.Add(new ReworkOrder(Guid.NewGuid(), id, batch.Id, row.StationId, row.ProductId,
                row.SampleId, batch.RecipeVersionId, batch.RecipeBundleHash, request.Note, user.Id, user.DisplayName, now));
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Results.Created($"/api/quality/inspections/{id}", await ReadDetails(id, row.ReworkOrderId, db, token));
    }

    private static async Task<IResult> Repeat(InspectionReview existing, CreateInspectionReviewRequest request,
        string userId, Guid? sourceOrderId, BoardTraceDbContext db, CancellationToken token) =>
        existing.ReviewedById == userId && existing.Disposition == request.Disposition && existing.Note == request.Note
            ? Results.Ok(await ReadDetails(existing.InspectionId, sourceOrderId, db, token))
            : Conflict("该检测已有最终复核，不能替换原人员或处置内容。");

    private static IResult Conflict(string reason) => Results.Problem(statusCode: 409, title: reason);
}
