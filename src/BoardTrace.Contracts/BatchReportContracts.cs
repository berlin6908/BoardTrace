namespace BoardTrace.Contracts;

public sealed record BatchCounts(int FirstArticle, int Production, int Reinspection);

public sealed record BatchClosureAudit(Guid BatchId, Guid ArchiveId, string ClosedById,
    string ClosedByName, DateTimeOffset ClosedAt);

public sealed record BatchClosureCheck(Guid BatchId, BatchStatus Status, bool CanClose,
    IReadOnlyList<string> Blockers, BatchCounts CentralCounts, StationRuntimeView? Station,
    int UnreviewedCount, int PendingReworkOrders, BatchClosureAudit? Closure);

public sealed record BatchPurposeStatistics(InspectionPurpose Purpose, int Attempts, int Completed,
    int MachinePass, int MachineFail, int TechnicalFailures);

public sealed record FirstInspectionYield(int EvaluatedProducts, int PassedProducts, int FailedProducts,
    double? PassRate);

public sealed record BatchSourceStatistics(string SourceKind, IReadOnlyList<BatchPurposeStatistics> Purposes,
    FirstInspectionYield FirstInspection);

public sealed record ReviewDispositionCount(ReviewDisposition Disposition, int Count);

public sealed record BatchReportSummary(IReadOnlyList<BatchPurposeStatistics> Purposes,
    FirstInspectionYield FirstInspection, IReadOnlyList<BatchSourceStatistics> BySource,
    IReadOnlyList<ReviewDispositionCount> Reviews);

public sealed record BatchReportRow(InspectionRecord Inspection, string ContentHash, DateTimeOffset ReceivedAt,
    InspectionQualityDetails Quality, string? TestedImageUrl, string? ReferenceImageUrl);

public sealed record BatchOperationalReport(BatchDefinition Batch, BatchStatus Status, Guid? ArchiveId,
    FirstArticleApproval? Approval, BatchClosureAudit? Closure, BatchReportSummary Summary,
    IReadOnlyList<BatchReportRow> Inspections, string Scope);
