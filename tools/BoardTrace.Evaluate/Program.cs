using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Vision;

var runWatch = Stopwatch.StartNew();
if (args.Length == 0 || args.Length % 2 != 0)
    throw new ArgumentException("--manifest <inputs.jsonl> --data-root <data> --output <predictions.jsonl> with --recipe <settings.json> or --model <onnx> --model-sha256 <sha> --score-thresholds <six comma-separated values in class order 1..6>");
var options = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1]);
var knownOptions = new[] { "--manifest", "--data-root", "--output", "--recipe", "--model", "--model-sha256", "--score-thresholds" };
if (options.Keys.Any(key => !knownOptions.Contains(key))) throw new ArgumentException("Unknown evaluation option.");
var modelMode = options.ContainsKey("--model");
if (modelMode == options.ContainsKey("--recipe"))
    throw new ArgumentException("Select exactly one of --recipe and --model.");
if (!modelMode && (options.ContainsKey("--model-sha256") || options.ContainsKey("--score-thresholds")))
    throw new ArgumentException("--model-sha256 and --score-thresholds require --model.");
if (modelMode && (!options.ContainsKey("--model-sha256") || !options.ContainsKey("--score-thresholds")))
    throw new ArgumentException("--model requires --model-sha256 and --score-thresholds.");
var scoreThresholds = modelMode ? OnnxScoreThresholds.FromClassOrder(options["--score-thresholds"].Split(',')
    .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray()) : null;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var initializationWatch = Stopwatch.StartNew();
using var onnx = modelMode ? new OnnxDetector(options["--model"], options["--model-sha256"]) : null;
var classical = modelMode ? null : new ClassicalDetector(
    JsonSerializer.Deserialize<ClassicalSettings>(File.ReadAllText(options["--recipe"]), json)
        ?? throw new InvalidDataException("Recipe is empty."));
var detectorInitializationMs = initializationWatch.Elapsed.TotalMilliseconds;
var outputPath = Path.GetFullPath(options["--output"]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var temporaryPath = outputPath + ".partial";
var completed = 0;
var failures = 0;
await using (var output = new StreamWriter(temporaryPath))
{
    foreach (var line in File.ReadLines(options["--manifest"]))
    {
        var input = JsonSerializer.Deserialize<ReplayInput>(line, json) ?? throw new InvalidDataException("Input row is empty.");
        object row;
        var attemptWatch = Stopwatch.StartNew();
        try
        {
            var tested = await File.ReadAllBytesAsync(Path.Combine(options["--data-root"], input.Image));
            var reference = await File.ReadAllBytesAsync(Path.Combine(options["--data-root"],
                input.Reference ?? throw new InvalidDataException("Input requires a reference image.")));
            DetectionResult result;
            if (onnx is not null)
                result = onnx.Detect(tested, reference, scoreThresholds!);
            else
                result = classical!.Detect(tested, reference);
            row = new { input.SampleId, execution = "Completed", result.Decision, result.Defects, result.ElapsedMs, result.Diagnostics,
                imageSha256 = Convert.ToHexStringLower(SHA256.HashData(tested)),
                referenceSha256 = Convert.ToHexStringLower(SHA256.HashData(reference)) };
            completed++;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OpenCvSharp.OpenCVException or ArgumentException)
        {
            row = new { input.SampleId, execution = "Failed", decision = "NotEvaluated", defects = Array.Empty<DetectedDefect>(),
                elapsedMs = attemptWatch.Elapsed.TotalMilliseconds, error = exception.Message };
            failures++;
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(row, json));
    }
}
File.Move(temporaryPath, outputPath, overwrite: true);
Console.WriteLine(JsonSerializer.Serialize(new { completed, failures, outputPath, detectorInitializationMs,
    sessionInitializationMs = onnx?.SessionInitializationMs, modelSha256 = onnx?.ModelSha256,
    scoreThresholds = scoreThresholds?.ToClassOrder(),
    totalElapsedMs = runWatch.Elapsed.TotalMilliseconds }, json));

internal sealed record ReplayInput(string SampleId, string Image, string? Reference);
