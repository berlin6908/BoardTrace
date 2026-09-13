using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Storage;
using BoardTrace.Vision;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Recipes;

public sealed class RecipeValidationWorker(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<RecipeValidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>();
            var interrupted = await db.ValidationRuns.Where(x => x.Status == "Running").ToListAsync(stoppingToken);
            foreach (var run in interrupted)
            {
                run.Status = "Failed";
                run.Error = "服务重启中断验证；请重新发起。";
                run.CompletedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>();
                var run = await db.ValidationRuns.Where(x => x.Status == "Queued")
                    .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(stoppingToken);
                if (run is null)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }
                run.Status = "Running";
                await db.SaveChangesAsync(stoppingToken);
                try
                {
                    run.ReportJson = JsonSerializer.Serialize(await ExecuteRun(run, db, stoppingToken));
                    run.Status = "Completed";
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    logger.LogError(error, "Recipe validation {RunId} failed", run.Id);
                    run.Status = "Failed";
                    run.Error = error.Message;
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<RecipeValidationReport> ExecuteRun(ValidationRun run, BoardTraceDbContext db,
        CancellationToken token)
    {
        var manifestRoot = config["RecipeValidation:ManifestRoot"] ?? throw new InvalidOperationException("验证清单目录未配置。");
        var dataRoot = config["RecipeValidation:DataRoot"] ?? throw new InvalidOperationException("验证图像目录未配置。");
        var inputPath = Path.Combine(manifestRoot, "inputs", "validation.jsonl");
        var truthPath = Path.Combine(manifestRoot, "truth", "validation.jsonl");
        if (await HashFile(inputPath, token) != run.ManifestHash || await HashFile(truthPath, token) != run.TruthHash)
            throw new InvalidDataException("验证清单在排队后发生变化。");
        var inputs = (await File.ReadAllLinesAsync(inputPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
        var truths = (await File.ReadAllLinesAsync(truthPath, token)).Select(line => JsonDocument.Parse(line))
            .ToDictionary(x => x.RootElement.GetProperty("sampleId").GetString()!);
        try
        {
            if (inputs.Length != 200 || truths.Count != 200) throw new InvalidDataException("验证人口不是固定 200 张。");
            var dto = JsonSerializer.Deserialize<RecipeClassicalSettings>(run.SettingsJson)!;
            var targets = JsonSerializer.Deserialize<RecipeTargets>(run.TargetsJson)!;
            var detector = new ClassicalDetector(new ClassicalSettings(dto.BinarizationThreshold, dto.EdgeTolerance,
                dto.MinimumArea, dto.ClosingSize, dto.BoxPadding, dto.MaximumTranslation,
                dto.MinimumAlignmentResponse));
            var rows = new List<RecipeValidationRow>(200);
            foreach (var input in inputs)
            {
                token.ThrowIfCancellationRequested();
                var sampleId = input.RootElement.GetProperty("sampleId").GetString()!;
                if (!truths.TryGetValue(sampleId, out var truth))
                    throw new InvalidDataException($"缺少真值行：{sampleId}");
                var truthBoxes = truth.RootElement.GetProperty("defects").EnumerateArray()
                    .Select(x => x.GetProperty("box").EnumerateArray().Select(n => n.GetDouble()).ToArray()).ToArray();
                RecipeValidationRow row;
                try
                {
                    var tested = await ReadAndCheck(input.RootElement, "image", "imageSha256", dataRoot, token);
                    var reference = await ReadAndCheck(input.RootElement, "reference", "referenceSha256", dataRoot, token);
                    var result = detector.Detect(tested, reference, token);
                    var predictions = result.Defects.Select(x => new RecipePrediction(x.Box, x.ClassId, x.Score, x.Area)).ToArray();
                    var (tp, fp, fn) = Score(predictions, truthBoxes);
                    row = new(sampleId, "Completed", result.Decision, predictions, null, result.ElapsedMs, tp, fp, fn);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or OpenCvSharp.OpenCVException or ArgumentException)
                {
                    row = new(sampleId, "Failed", "NotEvaluated", [], error.Message, null, 0, 0, truthBoxes.Length);
                }
                rows.Add(row);
                run.Processed = rows.Count;
                await db.SaveChangesAsync(token);
            }
            var tpAll = rows.Sum(x => x.Tp);
            var fpAll = rows.Sum(x => x.Fp);
            var fnAll = rows.Sum(x => x.Fn);
            var precision = tpAll + fpAll == 0 ? 0 : (double)tpAll / (tpAll + fpAll);
            var recall = tpAll + fnAll == 0 ? 0 : (double)tpAll / (tpAll + fnAll);
            var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
            var timed = rows.Where(x => x.ElapsedMs.HasValue).Select(x => x.ElapsedMs!.Value).ToArray();
            var warm = timed.Skip(1).Order().ToArray();
            var p50 = Percentile(warm, 0.5);
            var p95 = Percentile(warm, 0.95);
            var failures = rows.Count(x => x.Status == "Failed");
            var assemblyPath = typeof(ClassicalDetector).Assembly.Location;
            var assemblyHash = await HashFile(assemblyPath, token);
            return new(tpAll, fpAll, fnAll, precision, recall, f1, timed.Length == 0 ? null : timed[0],
                p50, p95, failures, failures == 0 && precision >= targets.MinPrecision &&
                recall >= targets.MinRecall && p95 <= targets.MaxP95Ms, rows, assemblyHash,
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                $"{Environment.MachineName}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; CPUs={Environment.ProcessorCount}",
                "首个成功检测样本单列冷启动；p50/p95 为其余成功样本的解码、配准、检测和后处理耗时。失败图不计耗时分位，但计入 200 张人口及 FN。");
        }
        finally
        {
            foreach (var item in inputs) item.Dispose();
            foreach (var item in truths.Values) item.Dispose();
        }
    }

    private static async Task<byte[]> ReadAndCheck(JsonElement row, string pathName, string hashName,
        string root, CancellationToken token)
    {
        var relative = row.GetProperty(pathName).GetString()!;
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("图像路径越出数据目录。");
        var bytes = await File.ReadAllBytesAsync(path, token);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != row.GetProperty(hashName).GetString())
            throw new InvalidDataException($"图像哈希不符：{relative}");
        return bytes;
    }

    private static async Task<string> HashFile(string path, CancellationToken token) =>
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, token)));

    private static (int Tp, int Fp, int Fn) Score(IReadOnlyList<RecipePrediction> predictions, double[][] truth)
    {
        var used = new bool[truth.Length];
        var tp = 0;
        var fp = 0;
        foreach (var prediction in predictions.OrderByDescending(x => x.Score))
        {
            var bestIndex = -1;
            var best = 0.5;
            for (var i = 0; i < truth.Length; i++)
            {
                if (used[i]) continue;
                var overlap = IoU(prediction.Box, truth[i]);
                if (overlap >= 0.5 && (bestIndex < 0 || overlap > best))
                {
                    best = overlap;
                    bestIndex = i;
                }
            }
            if (bestIndex >= 0) { used[bestIndex] = true; tp++; }
            else fp++;
        }
        return (tp, fp, truth.Length - tp);
    }

    private static double IoU(double[] a, double[] b)
    {
        var width = Math.Max(0, Math.Min(a[2], b[2]) - Math.Max(a[0], b[0]));
        var height = Math.Max(0, Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]));
        var intersection = width * height;
        var areaA = Math.Max(0, a[2] - a[0]) * Math.Max(0, a[3] - a[1]);
        var areaB = Math.Max(0, b[2] - b[0]) * Math.Max(0, b[3] - b[1]);
        var union = areaA + areaB - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static double Percentile(double[] values, double q)
    {
        if (values.Length == 0) return 0;
        var index = (values.Length - 1) * q;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return values[lower] + (values[upper] - values[lower]) * (index - lower);
    }
}
