using BoardTrace.Contracts;
using System.Text.Json;

namespace BoardTrace.Server.Inspections;

public sealed class InspectionAttempt
{
    public Guid Id { get; set; }
    public string StationId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public InspectionPurpose Purpose { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? ExecutionSessionId { get; set; }
    public int? ProductionSequence { get; set; }
    public Guid? ControllerSessionId { get; set; }
    public long? TriggerSequence { get; set; }
    public string OperatorId { get; set; } = "";
    public string OperatorName { get; set; } = "";
    public string SampleId { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string RecipeId { get; set; } = "";
    public string RecipeJson { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public InspectionExecution ExecutionStatus { get; set; }
    public QualityDecision Decision { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double? DetectionMs { get; set; }
    public string DiagnosticsJson { get; set; } = "{}";
    public string? Error { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public List<InspectionDefect> Defects { get; set; } = [];
    public List<InspectionImage> Images { get; set; } = [];

    public static InspectionAttempt From(InspectionRecord record, string hash)
    {
        var inspection = new InspectionAttempt
        {
            Id = record.Id, StationId = record.StationId, ProductId = record.ProductId,
            Purpose = record.Purpose, BatchId = record.BatchId, ExecutionSessionId = record.ExecutionSessionId,
            ProductionSequence = record.ProductionSequence,
            ControllerSessionId = record.ControllerSessionId, TriggerSequence = record.TriggerSequence,
            OperatorId = record.OperatorId, OperatorName = record.OperatorName,
            SampleId = record.SampleId, SourceKind = record.SourceKind, RecipeId = record.RecipeId,
            RecipeJson = record.RecipeJson, StartedAt = record.StartedAt, CompletedAt = record.CompletedAt!.Value,
            ExecutionStatus = record.ExecutionStatus, Decision = record.Decision, Width = record.Width,
            Height = record.Height, DetectionMs = record.DetectionMs, Error = record.Error,
            DiagnosticsJson = JsonSerializer.Serialize(record.Diagnostics), ContentHash = hash, ReceivedAt = DateTimeOffset.UtcNow
        };
        inspection.Defects = record.Defects.Select((d, index) => new InspectionDefect
        {
            InspectionId = record.Id, Ordinal = index, Left = d.Box[0], Top = d.Box[1], Right = d.Box[2], Bottom = d.Box[3],
            ClassId = d.ClassId, Score = d.Score, Area = d.Area
        }).ToList();
        if (record.TestedImage != null) inspection.Images.Add(new InspectionImage { InspectionId = record.Id, Kind = "tested", Content = record.TestedImage });
        if (record.ReferenceImage != null) inspection.Images.Add(new InspectionImage { InspectionId = record.Id, Kind = "reference", Content = record.ReferenceImage });
        return inspection;
    }

    public InspectionRecord ToRecord() => new()
    {
        Id = Id, StationId = StationId, ProductId = ProductId, OperatorId = OperatorId, OperatorName = OperatorName,
        Purpose = Purpose, BatchId = BatchId, ExecutionSessionId = ExecutionSessionId, ProductionSequence = ProductionSequence,
        ControllerSessionId = ControllerSessionId, TriggerSequence = TriggerSequence is null ? null : checked((uint)TriggerSequence.Value),
        SampleId = SampleId, SourceKind = SourceKind,
        RecipeId = RecipeId, RecipeJson = RecipeJson, StartedAt = StartedAt, CompletedAt = CompletedAt,
        ExecutionStatus = ExecutionStatus, Decision = Decision, Width = Width, Height = Height,
        DetectionMs = DetectionMs, Error = Error,
        Diagnostics = JsonSerializer.Deserialize<Dictionary<string, double>>(DiagnosticsJson)!,
        Defects = Defects.OrderBy(d => d.Ordinal).Select(d => new DefectBox([d.Left, d.Top, d.Right, d.Bottom], d.ClassId, d.Score, d.Area)).ToArray()
    };

    public InspectionReceipt Receipt() => new(Id, ContentHash, ReceivedAt);
}

public sealed class InspectionDefect
{
    public Guid InspectionId { get; set; }
    public int Ordinal { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
    public int? ClassId { get; set; }
    public double Score { get; set; }
    public int Area { get; set; }
}

public sealed class InspectionImage
{
    public Guid InspectionId { get; set; }
    public string Kind { get; set; } = "";
    public byte[] Content { get; set; } = [];
}
