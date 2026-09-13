namespace BoardTrace.Contracts;

public sealed record RecipeClassicalSettings(int BinarizationThreshold = 127, int EdgeTolerance = 1,
    int MinimumArea = 8, int ClosingSize = 3, int BoxPadding = 10,
    double MaximumTranslation = 12, double MinimumAlignmentResponse = 0.1);

public sealed record RecipeTargets(double MinPrecision, double MinRecall, double MaxP95Ms);

public sealed record SaveRecipeDraft(string Name, RecipeClassicalSettings Settings, RecipeTargets Targets);

public sealed record RecipeDraftView(Guid Id, string Name, string Algorithm, RecipeClassicalSettings Settings,
    RecipeTargets Targets, string DataManifestSha256, string SnapshotHash, DateTimeOffset UpdatedAt);

public sealed record RecipeValidationRow(string SampleId, string Status, string Decision,
    IReadOnlyList<RecipePrediction> Defects, string? Error, double? ElapsedMs, int Tp, int Fp, int Fn);

public sealed record RecipePrediction(double[] Box, int? ClassId, double Score, int Area);

public sealed record RecipeValidationReport(int Tp, int Fp, int Fn, double Precision, double Recall, double F1,
    double? ColdSampleMs, double P50Ms, double P95Ms, int ExecutionFailures, bool MeetsTargets,
    IReadOnlyList<RecipeValidationRow> Rows, string AlgorithmAssemblySha256, string Runtime,
    string Machine, string TimingDescription);

public sealed record RecipeValidationSnapshot(string Name, RecipeClassicalSettings Settings,
    RecipeTargets Targets, string DataManifestSha256, string SnapshotHash);

public sealed record RecipeValidationView(Guid Id, Guid DraftId, string Status, int Processed, int Total,
    RecipeValidationSnapshot Snapshot, RecipeValidationReport? Report, string? Error, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
