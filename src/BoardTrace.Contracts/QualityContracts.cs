using System.Text.Json.Serialization;

namespace BoardTrace.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ReviewDisposition>))]
public enum ReviewDisposition { Accept, Reject, Rework, ResolveTechnicalIssue }

public sealed record CreateInspectionReviewRequest(ReviewDisposition Disposition, string Note);

public sealed record InspectionReview(Guid InspectionId, ReviewDisposition Disposition, string Note,
    string ReviewedById, string ReviewedByName, DateTimeOffset ReviewedAt);

public sealed record ReworkOrder(Guid Id, Guid OriginalInspectionId, Guid BatchId, string StationId,
    string ProductId, string SampleId, Guid RecipeVersionId, string RecipeBundleHash, string Reason,
    string CreatedById, string CreatedByName, DateTimeOffset CreatedAt);

public sealed record InspectionQualityDetails(InspectionReview? Review, ReworkOrder? SourceReworkOrder,
    ReworkOrder? ReworkOrder, Guid? ReinspectionId);

public sealed record QualityQueueItem(Guid InspectionId, Guid BatchId, string BatchNumber, string StationId,
    string ProductId, string SampleId, InspectionPurpose Purpose, InspectionExecution ExecutionStatus,
    QualityDecision Decision, DateTimeOffset StartedAt);

public sealed record QualityQueuePage(IReadOnlyList<QualityQueueItem> Items, int Total, int Page, int PageSize);
