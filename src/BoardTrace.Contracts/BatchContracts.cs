using System.Text.Json.Serialization;

namespace BoardTrace.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<BatchStatus>))]
public enum BatchStatus { AwaitingFirstArticle, Approved, InProgress, Closed }

public sealed record CreateBatchRequest(string BatchNumber, string ProductType, string FieldOfView,
    int PlannedQuantity, string StationId, Guid RecipeVersionId);

public sealed record BatchDefinition(Guid Id, string BatchNumber, string ProductType, string FieldOfView,
    int PlannedQuantity, string StationId, Guid RecipeVersionId, string RecipeBundleHash,
    string CreatedById, string CreatedByName, DateTimeOffset CreatedAt);

public sealed record FirstArticleApproval(Guid BatchId, Guid InspectionId, string ApprovedById,
    string ApprovedByName, DateTimeOffset ApprovedAt);

public sealed record ApproveFirstArticleRequest(Guid InspectionId);

public sealed record StartBatchRequest(string StationId, string RecipeBundleHash, Guid ArchiveId);

public sealed record BatchExecutionSession(Guid Id, Guid BatchId, string StationId, string RecipeBundleHash,
    Guid FirstArticleInspectionId, string OperatorId, string OperatorName, DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt, Guid ArchiveId);

public sealed record BatchSummary(BatchDefinition Batch, BatchStatus Status, int ReceivedProductionCount);

public sealed record BatchInspectionSummary(Guid Id, string ProductId, string SourceKind, string OperatorName,
    InspectionExecution ExecutionStatus, QualityDecision Decision, DateTimeOffset StartedAt);

public sealed record BatchDetails(BatchDefinition Batch, BatchStatus Status, FirstArticleApproval? Approval,
    IReadOnlyList<BatchInspectionSummary> FirstArticles, int ReceivedProductionCount, int TechnicalFailureCount);

public sealed record BatchPackage(BatchDefinition Batch, BatchStatus Status, FirstArticleApproval? Approval,
    PublishedRecipeVersion Recipe);

public sealed record StationSummary(string StationId, string DisplayName);
