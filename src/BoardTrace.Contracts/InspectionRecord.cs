using System.Text.Json.Serialization;

namespace BoardTrace.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<InspectionExecution>))]
public enum InspectionExecution { Started, Completed, Failed, Interrupted }

[JsonConverter(typeof(JsonStringEnumConverter<QualityDecision>))]
public enum QualityDecision { NotEvaluated, Pass, Fail }

public sealed record DefectBox(double[] Box, int? ClassId, double Score, int Area);

public sealed record InspectionRecord
{
    public required Guid Id { get; init; }
    public required string StationId { get; init; }
    public required string ProductId { get; init; }
    public required string SampleId { get; init; }
    public required string SourceKind { get; init; }
    public required string RecipeId { get; init; }
    public required string RecipeJson { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public InspectionExecution ExecutionStatus { get; init; } = InspectionExecution.Started;
    public QualityDecision Decision { get; init; } = QualityDecision.NotEvaluated;
    public int Width { get; init; }
    public int Height { get; init; }
    public IReadOnlyList<DefectBox> Defects { get; init; } = [];
    public IReadOnlyDictionary<string, double> Diagnostics { get; init; } = new Dictionary<string, double>();
    public double? DetectionMs { get; init; }
    public string? Error { get; init; }
    public byte[]? TestedImage { get; init; }
    public byte[]? ReferenceImage { get; init; }
}
