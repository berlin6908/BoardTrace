using BoardTrace.Contracts;
using BoardTrace.Server.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using BoardTrace.Server.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using BoardTrace.Server.Batches;

namespace BoardTrace.Server.Inspections;

public sealed record InspectionSummary(Guid Id, string StationId, string ProductId, InspectionPurpose Purpose, Guid? BatchId, int? ProductionSequence, string SampleId, string SourceKind,
    string RecipeId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, InspectionExecution ExecutionStatus,
    QualityDecision Decision, int DefectCount, double? DetectionMs, DateTimeOffset ReceivedAt);

public sealed record InspectionDetail(InspectionRecord Inspection, DateTimeOffset ReceivedAt, string ContentHash,
    bool HasTestedImage, bool HasReferenceImage);

public static class InspectionEndpoints
{
    public static void MapInspections(this IEndpointRouteBuilder app)
    {
        var inspections = app.MapGroup("/api/inspections");
        inspections.MapPut("/{id:guid}", ReceiveAsync).RequireAuthorization("Station");
        inspections.MapGet("/", ListAsync).RequireAuthorization("Human");
        inspections.MapGet("/{id:guid}", DetailAsync).RequireAuthorization("Human");
        inspections.MapGet("/{id:guid}/images/{kind}", ImageAsync).RequireAuthorization("Human");
    }

    private static async Task<IResult> ReceiveAsync(Guid id, InspectionRecord record, BoardTraceDbContext db,
        ClaimsPrincipal principal, UserManager<BoardTraceUser> users, CancellationToken cancellationToken)
    {
        var station = await users.GetUserAsync(principal);
        if (station?.StationId != record.StationId) return Results.Forbid();
        var existing = await ReadReceiptAsync(db, id, cancellationToken);
        if (existing != null) return RepeatResult(existing, InspectionTransfer.Hash(record));
        if (InspectionValidation.Validate(id, record) is string problem)
            return Results.Problem(statusCode: 400, title: "检测档案无效", detail: problem);
        if (await users.FindByIdAsync(record.OperatorId) is null)
            return Results.Problem(statusCode: 400, title: "操作员不存在");
        if (await BatchInspectionGate.Check(record, db, cancellationToken) is string gate)
            return Results.Problem(statusCode: 409, title: gate);
        var hash = InspectionTransfer.Hash(record);

        var inspection = InspectionAttempt.From(record, hash);
        db.Inspections.Add(inspection);
        try
        {
            // EF commits the header, every defect and both image BLOBs in one transaction.
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/inspections/{id}", inspection.Receipt());
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // A concurrent retry may commit after the initial lookup. The primary key is the final arbiter.
            db.ChangeTracker.Clear();
            existing = await ReadReceiptAsync(db, id, cancellationToken);
            if (existing is null)
            {
                if (record.ControllerSessionId is Guid session && record.TriggerSequence is uint sequence &&
                    await db.Inspections.AnyAsync(row => row.StationId == record.StationId &&
                        row.ControllerSessionId == session && row.TriggerSequence == sequence, cancellationToken))
                    return Results.Problem(statusCode: 409, title: "PLC 物理触发身份已被另一检测档案占用。");
                if (record.Purpose == InspectionPurpose.Production && await db.Inspections.AnyAsync(row => row.BatchId == record.BatchId && row.ProductionSequence == record.ProductionSequence, cancellationToken))
                    return Results.Problem(statusCode: 409, title: "该批次生产序号已被另一检测档案占用。");
                if (record.ReworkOrderId is Guid orderId && await db.Inspections.AnyAsync(row => row.ReworkOrderId == orderId, cancellationToken))
                    return Results.Problem(statusCode: 409, title: "该返工指令已有检测尝试，不能再次接件；技术失败或中断也消耗本次指令。");
                throw;
            }
            return RepeatResult(existing, hash);
        }
    }

    private static Task<InspectionReceipt?> ReadReceiptAsync(BoardTraceDbContext db, Guid id, CancellationToken token) =>
        db.Inspections.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new InspectionReceipt(x.Id, x.ContentHash, x.ReceivedAt)).SingleOrDefaultAsync(token);

    private static IResult RepeatResult(InspectionReceipt receipt, string hash) => receipt.ContentHash == hash
        ? Results.Ok(receipt)
        : Results.Problem(statusCode: 409, title: "检测编号冲突", detail: "同一检测编号已保存不同内容，原始档案不会被覆盖。");

    private static async Task<IResult> ListAsync(BoardTraceDbContext db, string? stationId, string? productId,
        QualityDecision? decision, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var number = page ?? 1;
        var size = pageSize ?? 20;
        if (number < 1 || number > 1_000_000 || size is < 1 or > 100 || decision is not null && !Enum.IsDefined(decision.Value))
            return Results.Problem(statusCode: 400, title: "查询参数无效", detail: "页码至少为 1，每页 1–100 条。判定需为 Pass、Fail 或 NotEvaluated。");
        var query = db.Inspections.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(stationId)) query = query.Where(x => x.StationId == stationId);
        if (!string.IsNullOrWhiteSpace(productId)) query = query.Where(x => x.ProductId.Contains(productId));
        if (decision != null) query = query.Where(x => x.Decision == decision);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id)
            .Skip((number - 1) * size).Take(size)
            .Select(x => new InspectionSummary(x.Id, x.StationId, x.ProductId, x.Purpose, x.BatchId, x.ProductionSequence, x.SampleId, x.SourceKind, x.RecipeId,
                x.StartedAt, x.CompletedAt, x.ExecutionStatus, x.Decision, x.Defects.Count, x.DetectionMs, x.ReceivedAt))
            .ToArrayAsync(cancellationToken);
        return Results.Ok(new { items, total, page = number, pageSize = size });
    }

    private static async Task<IResult> DetailAsync(Guid id, BoardTraceDbContext db, CancellationToken cancellationToken)
    {
        var inspection = await db.Inspections.AsNoTracking().Include(x => x.Defects).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (inspection is null) return Results.NotFound();
        var kinds = await db.Images.Where(x => x.InspectionId == id).Select(x => x.Kind).ToArrayAsync(cancellationToken);
        return Results.Ok(new InspectionDetail(inspection.ToRecord(), inspection.ReceivedAt, inspection.ContentHash,
            kinds.Contains("tested"), kinds.Contains("reference")));
    }

    private static async Task<IResult> ImageAsync(Guid id, string kind, BoardTraceDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        if (kind is not ("tested" or "reference")) return Results.NotFound();
        var bytes = await db.Images.Where(x => x.InspectionId == id && x.Kind == kind).Select(x => x.Content).SingleOrDefaultAsync(cancellationToken);
        if (bytes is null) return Results.NotFound();
        var contentType = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47
            ? "image/png" : bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8 ? "image/jpeg" : "application/octet-stream";
        context.Response.Headers.CacheControl = "private,no-store";
        return Results.Bytes(bytes, contentType);
    }
}
