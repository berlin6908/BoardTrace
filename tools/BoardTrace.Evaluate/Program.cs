using System.Text.Json;
using BoardTrace.Vision;

if (args.Length == 0 || args.Length % 2 != 0)
    throw new ArgumentException("--manifest <inputs.jsonl> --data-root <data> --recipe <settings.json> --output <predictions.jsonl>");
var options = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1]);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var settings = JsonSerializer.Deserialize<ClassicalSettings>(File.ReadAllText(options["--recipe"]), json)
    ?? throw new InvalidDataException("Recipe is empty.");
var detector = new ClassicalDetector(settings);
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
        try
        {
            var tested = await File.ReadAllBytesAsync(Path.Combine(options["--data-root"], input.Image));
            var reference = await File.ReadAllBytesAsync(Path.Combine(options["--data-root"], input.Reference));
            var result = detector.Detect(tested, reference);
            row = new { input.SampleId, execution = "Completed", result.Decision, result.Defects, result.ElapsedMs, result.Diagnostics };
            completed++;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OpenCvSharp.OpenCVException or ArgumentException)
        {
            row = new { input.SampleId, execution = "Failed", decision = "NotEvaluated", defects = Array.Empty<DetectedDefect>(), error = exception.Message };
            failures++;
        }
        await output.WriteLineAsync(JsonSerializer.Serialize(row, json));
    }
}
File.Move(temporaryPath, outputPath, overwrite: true);
Console.WriteLine(JsonSerializer.Serialize(new { completed, failures, outputPath }, json));

internal sealed record ReplayInput(string SampleId, string Image, string Reference);
