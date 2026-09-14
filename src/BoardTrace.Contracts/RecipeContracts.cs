using System.Text.Json.Serialization;

namespace BoardTrace.Contracts;

public sealed record RecipeClassicalSettings(int BinarizationThreshold = 127, int EdgeTolerance = 1,
    int MinimumArea = 8, int ClosingSize = 3, int BoxPadding = 10,
    double MaximumTranslation = 12, double MinimumAlignmentResponse = 0.1);

public sealed record RecipeTargets(double MinPrecision, double MinRecall, double MaxP95Ms);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "algorithm")]
[JsonDerivedType(typeof(ClassicalRecipeDefinition), "Classical")]
[JsonDerivedType(typeof(PairedOnnxRecipeDefinition), "PairedOnnx")]
public abstract record RecipeDefinition
{
    [JsonIgnore]
    public string Algorithm => this switch
    {
        ClassicalRecipeDefinition => "Classical",
        PairedOnnxRecipeDefinition => "PairedOnnx",
        _ => throw new InvalidOperationException("Unsupported recipe definition.")
    };
}

public sealed record ClassicalRecipeDefinition(RecipeClassicalSettings Settings) : RecipeDefinition;

public sealed record PairedOnnxRecipeDefinition(string ModelSha256, RecipeScoreThresholds Thresholds) : RecipeDefinition;

public sealed record RecipeScoreThresholds(
    [property: JsonRequired] double Open, [property: JsonRequired] double Short,
    [property: JsonRequired] double Mousebite, [property: JsonRequired] double Spur,
    [property: JsonRequired] double Copper, [property: JsonRequired] double PinHole);

public static class RecipeModelInput
{
    // float32 images[1,3,640,640]: tested gray, reference gray, absdiff, each /255.
    // Model contains mean/std .5 normalization and NMS; xyxy boxes, labels 1–6, scores.
    public const string PairedGrayAbsDiff640V1 = "PairedGrayAbsDiff640V1";
}

public sealed record RecipeModelSummary(string Sha256, int ByteLength, string InputContract, DateTimeOffset CreatedAt);

public sealed record SaveRecipeDraft(string Name, RecipeDefinition Definition, RecipeTargets Targets);

public sealed record RecipeDraftView(Guid Id, string Name, RecipeDefinition Definition,
    RecipeTargets Targets, string DataManifestSha256, string SnapshotHash, DateTimeOffset UpdatedAt);

public sealed record RecipeValidationRow(string SampleId, string Status, string Decision,
    IReadOnlyList<RecipePrediction> Defects, string? Error, double? ElapsedMs, int Tp, int Fp, int Fn);

public sealed record RecipePrediction(double[] Box, int? ClassId, double Score, int Area);

public sealed record RecipeClassMetrics(int ClassId, int Tp, int Fp, int Fn, double Precision, double Recall, double F1);

public sealed record RecipeValidationReport(int Tp, int Fp, int Fn, double Precision, double Recall, double F1,
    double? ColdSampleMs, double P50Ms, double P95Ms, int ExecutionFailures, bool MeetsTargets,
    IReadOnlyList<RecipeValidationRow> Rows, string AlgorithmAssemblySha256, string Runtime,
    string Machine, string TimingDescription, string MatchingMode, IReadOnlyList<RecipeClassMetrics> Classes,
    double? SessionInitializationMs, string? ModelSha256);

public sealed record RecipeValidationSnapshot(string Name, RecipeDefinition Definition,
    RecipeTargets Targets, string DataManifestSha256, string SnapshotHash);

public sealed record RecipeValidationView(Guid Id, Guid DraftId, string Status, int Processed, int Total,
    RecipeValidationSnapshot Snapshot, RecipeValidationReport? Report, string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
