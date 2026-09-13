using BoardTrace.Contracts;

namespace BoardTrace.Server.Batches;

public sealed class BatchEntity
{
    public Guid Id { get; set; }
    public required string BatchNumber { get; set; }
    public required string ProductType { get; set; }
    public required string FieldOfView { get; set; }
    public int PlannedQuantity { get; set; }
    public required string StationId { get; set; }
    public Guid RecipeVersionId { get; set; }
    public required string RecipeBundleHash { get; set; }
    public required string CreatedById { get; set; }
    public required string CreatedByName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public BatchStatus Status { get; set; }

    public BatchDefinition Definition() => new(Id, BatchNumber, ProductType, FieldOfView, PlannedQuantity,
        StationId, RecipeVersionId, RecipeBundleHash, CreatedById, CreatedByName, CreatedAt);

    public bool Matches(CreateBatchRequest request) => BatchNumber == request.BatchNumber && ProductType == request.ProductType &&
        FieldOfView == request.FieldOfView && PlannedQuantity == request.PlannedQuantity && StationId == request.StationId && RecipeVersionId == request.RecipeVersionId;
}
