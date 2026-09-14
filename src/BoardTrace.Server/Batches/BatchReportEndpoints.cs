using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Inspections;
using BoardTrace.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Batches;

public static class BatchReportEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly InspectionPurpose[] Purposes = [InspectionPurpose.FirstArticle, InspectionPurpose.Production, InspectionPurpose.Reinspection];

    public static void MapBatchReports(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/batches/{id:guid}/report", Report).RequireAuthorization("Human");
        app.MapGet("/api/batches/{id:guid}/report.csv", Csv).RequireAuthorization("Human");
    }

    private static async Task<IResult> Report(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        var report = await Read(id, db, token);
        return report is null ? Results.NotFound() : Results.File(JsonSerializer.SerializeToUtf8Bytes(report, Json),
            "application/json; charset=utf-8", $"batch-{id:D}.json");
    }

    private static async Task<IResult> Csv(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        var report = await Read(id, db, token);
        if (report is null) return Results.NotFound();
        var csv = new StringBuilder();
        AddLine(csv, ["RowType", "BatchId", "BatchNumber", "StationId", "RecipeVersionId", "RecipeBundleHash", "ArchiveId", "BatchStatus",
            "FirstArticleAttempts", "ProductionAttempts", "ReinspectionAttempts", "FirstEvaluatedProducts", "FirstPassedProducts", "FirstPassRate",
            "ConstructedNormalFirstProducts", "ConstructedNormalFirstPassRate", "ClosedById", "ClosedByName", "ClosedAt",
            "InspectionId", "ProductId", "SampleId", "Purpose", "ProductionSequence", "SourceKind", "ExecutionStatus", "MachineDecision",
            "StartedAt", "CompletedAt", "OperatorId", "OperatorName", "ContentHash", "DefectsJson", "Error",
            "ReviewDisposition", "ReviewNote", "ReviewedById", "ReviewedByName", "ReviewedAt", "SourceReworkOrderId", "OriginalInspectionId",
            "NextReworkOrderId", "ReinspectionId", "TestedImageUrl", "ReferenceImageUrl", "Scope"]);
        var normal = report.Summary.BySource.SingleOrDefault(source => source.SourceKind == "ConstructedNormal")?.FirstInspection;
        var rows = report.Inspections.Cast<BatchReportRow?>().DefaultIfEmpty();
        foreach (var row in rows)
        {
            var inspection = row?.Inspection;
            var review = row?.Quality.Review;
            AddLine(csv, [row is null ? "EmptyBatch" : "Inspection", report.Batch.Id, report.Batch.BatchNumber, report.Batch.StationId,
                report.Batch.RecipeVersionId, report.Batch.RecipeBundleHash, report.ArchiveId, report.Status,
                report.Summary.Purposes.Single(p => p.Purpose == InspectionPurpose.FirstArticle).Attempts,
                report.Summary.Purposes.Single(p => p.Purpose == InspectionPurpose.Production).Attempts,
                report.Summary.Purposes.Single(p => p.Purpose == InspectionPurpose.Reinspection).Attempts,
                report.Summary.FirstInspection.EvaluatedProducts, report.Summary.FirstInspection.PassedProducts, report.Summary.FirstInspection.PassRate,
                normal?.EvaluatedProducts ?? 0, normal?.PassRate, report.Closure?.ClosedById, report.Closure?.ClosedByName, report.Closure?.ClosedAt,
                inspection?.Id, inspection?.ProductId, inspection?.SampleId, inspection?.Purpose, inspection?.ProductionSequence,
                inspection?.SourceKind, inspection?.ExecutionStatus, inspection?.Decision, inspection?.StartedAt, inspection?.CompletedAt,
                inspection?.OperatorId, inspection?.OperatorName, row?.ContentHash,
                inspection is null ? null : JsonSerializer.Serialize(inspection.Defects, Json), inspection?.Error,
                review?.Disposition, review?.Note, review?.ReviewedById, review?.ReviewedByName, review?.ReviewedAt,
                row?.Quality.SourceReworkOrder?.Id, row?.Quality.SourceReworkOrder?.OriginalInspectionId,
                row?.Quality.ReworkOrder?.Id, row?.Quality.ReinspectionId, row?.TestedImageUrl, row?.ReferenceImageUrl, report.Scope]);
        }
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
            "text/csv; charset=utf-8", $"batch-{id:D}.csv");
    }

    private static void AddLine(StringBuilder csv, object?[] values)
    {
        csv.AppendJoin(',', values.Select(value =>
        {
            var text = value switch { null => "", DateTimeOffset time => time.ToString("O"), IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()! };
            // Keep exported user text as text when opened in spreadsheet applications.
            if (value is string && text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
            return '"' + text.Replace("\"", "\"\"") + '"';
        }));
        csv.Append("\r\n");
    }

    private static async Task<BatchOperationalReport?> Read(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        // The batch read lock keeps receipt/review/closure writers out of this report snapshot.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(batch => batch.Id == id, token);
        if (batch is null) return null;
        var attempts = await db.Inspections.AsNoTracking().Where(row => row.BatchId == id).Include(row => row.Defects)
            .OrderBy(row => row.StartedAt).ThenBy(row => row.Id).ToArrayAsync(token);
        var reviews = await db.InspectionReviews.AsNoTracking().Where(review => db.Inspections.Any(row => row.Id == review.InspectionId && row.BatchId == id))
            .ToDictionaryAsync(review => review.InspectionId, token);
        var orders = await db.ReworkOrders.AsNoTracking().Where(order => order.BatchId == id).ToArrayAsync(token);
        var images = await db.Images.Where(image => db.Inspections.Any(row => row.Id == image.InspectionId && row.BatchId == id))
            .Select(image => new { image.InspectionId, image.Kind }).ToArrayAsync(token);
        var first = attempts.Where(row => row.Purpose == InspectionPurpose.Production && row.ExecutionStatus == InspectionExecution.Completed)
            .OrderBy(row => row.ProductionSequence).ThenBy(row => row.Id).GroupBy(row => row.ProductId, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var summary = new BatchReportSummary(Statistics(attempts), Yield(first), attempts.Select(row => row.SourceKind).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select(source => new BatchSourceStatistics(source,
                Statistics(attempts.Where(row => row.SourceKind == source)), Yield(first.Where(row => row.SourceKind == source)))).ToArray(),
            Enum.GetValues<ReviewDisposition>().Select(disposition => new ReviewDispositionCount(disposition, reviews.Values.Count(review => review.Disposition == disposition))).ToArray());
        var byId = orders.ToDictionary(order => order.Id);
        var byOriginal = orders.ToDictionary(order => order.OriginalInspectionId);
        var byOrder = attempts.Where(row => row.ReworkOrderId is not null).ToDictionary(row => row.ReworkOrderId!.Value, row => row.Id);
        var imageKinds = images.Select(image => (image.InspectionId, image.Kind)).ToHashSet();
        var rows = attempts.Select(row =>
        {
            var next = byOriginal.GetValueOrDefault(row.Id);
            return new BatchReportRow(row.ToRecord(), row.ContentHash, row.ReceivedAt,
                new InspectionQualityDetails(reviews.GetValueOrDefault(row.Id), row.ReworkOrderId is Guid orderId ? byId[orderId] : null,
                    next, next is not null && byOrder.TryGetValue(next.Id, out var returned) ? returned : null),
                imageKinds.Contains((row.Id, "tested")) ? $"/api/inspections/{row.Id:D}/images/tested" : null,
                imageKinds.Contains((row.Id, "reference")) ? $"/api/inspections/{row.Id:D}/images/reference" : null);
        }).ToArray();
        var approval = await db.FirstArticleApprovals.AsNoTracking().SingleOrDefaultAsync(row => row.BatchId == id, token);
        var closure = await db.BatchClosures.AsNoTracking().SingleOrDefaultAsync(row => row.BatchId == id, token);
        await transaction.CommitAsync(token);
        return new(batch.Definition(), batch.Status, batch.ArchiveId, approval, closure, summary, rows,
            "模拟业务报告。首检为每个产品按生产序号排序的首次有效Completed Production；技术失败与复检不进入该分母。ConstructedNormal单列，不能据此宣称真实产线良率。");
    }

    private static BatchPurposeStatistics[] Statistics(IEnumerable<InspectionAttempt> attempts)
    {
        var rows = attempts.ToArray();
        return Purposes.Select(purpose =>
        {
            var selected = rows.Where(row => row.Purpose == purpose).ToArray();
            return new BatchPurposeStatistics(purpose, selected.Length, selected.Count(row => row.ExecutionStatus == InspectionExecution.Completed),
                selected.Count(row => row.Decision == QualityDecision.Pass), selected.Count(row => row.Decision == QualityDecision.Fail),
                selected.Count(row => row.ExecutionStatus is InspectionExecution.Failed or InspectionExecution.Interrupted));
        }).ToArray();
    }

    private static FirstInspectionYield Yield(IEnumerable<InspectionAttempt> attempts)
    {
        var rows = attempts.ToArray();
        var passed = rows.Count(row => row.Decision == QualityDecision.Pass);
        return new(rows.Length, passed, rows.Length - passed, rows.Length == 0 ? null : (double)passed / rows.Length);
    }
}
