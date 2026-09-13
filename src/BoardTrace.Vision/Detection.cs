namespace BoardTrace.Vision;

public sealed record DetectedDefect(double[] Box, int? ClassId, double Score, int Area);

public sealed record DetectionResult(
    string Decision,
    int Width,
    int Height,
    IReadOnlyList<DetectedDefect> Defects,
    double ElapsedMs,
    IReadOnlyDictionary<string, double> Diagnostics);

public sealed record ClassicalSettings(
    int BinarizationThreshold = 127,
    int EdgeTolerance = 1,
    int MinimumArea = 8,
    int ClosingSize = 3,
    int BoxPadding = 10,
    double MaximumTranslation = 12,
    double MinimumAlignmentResponse = 0.1);
