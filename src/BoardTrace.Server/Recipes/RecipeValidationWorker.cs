using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Storage;
using BoardTrace.Vision;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Recipes;

public sealed class RecipeValidationWorker(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<RecipeValidationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recoverInterrupted = true;
        var databaseInterrupted = false;
        Guid? activeRunId = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>();
                if (recoverInterrupted)
                {
                    // A failed write may have committed. Re-read durable state in a new
                    // context: preserve a committed completion, never replay unfinished work.
                    var interrupted = await db.ValidationRuns.Where(x => x.Status == "Running" ||
                        (x.Id == activeRunId && x.Status == "Queued")).ToListAsync(stoppingToken);
                    foreach (var item in interrupted)
                    {
                        item.Status = "Failed";
                        item.Error = databaseInterrupted ? "数据库连接中断验证；请重新发起。" : "服务重启中断验证；请重新发起。";
                        item.ReportJson = null;
                        item.CompletedAt = DateTimeOffset.UtcNow;
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    if (databaseInterrupted) logger.LogInformation("Recipe validation database recovered; unfinished interrupted work was marked failed");
                    recoverInterrupted = false;
                    databaseInterrupted = false;
                    activeRunId = null;
                }
                var run = await db.ValidationRuns.Where(x => x.Status == "Queued")
                    .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(stoppingToken);
                if (run is null)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }
                activeRunId = run.Id;
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
                catch (Exception error) when (!IsDatabaseFailure(error))
                {
                    logger.LogError(error, "Recipe validation {RunId} failed", run.Id);
                    run.Status = "Failed";
                    run.Error = error.Message;
                    run.ReportJson = null;
                    run.CompletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
                activeRunId = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (IsDatabaseFailure(error))
            {
                if (!databaseInterrupted)
                    logger.LogError(error, "Recipe validation database unavailable; pausing worker before recovering run {RunId}", activeRunId);
                databaseInterrupted = true;
                recoverInterrupted = true;
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    // EF can wrap the provider exception in DbUpdateException or InvalidOperationException.
    private static bool IsDatabaseFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SqlException) return true;
        return false;
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
            var definition = JsonSerializer.Deserialize<RecipeDefinition>(run.DefinitionJson)!;
            var targets = JsonSerializer.Deserialize<RecipeTargets>(run.TargetsJson)!;
            var paired = definition as PairedOnnxRecipeDefinition;
            var model = paired is null ? null : await db.RecipeModels.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Sha256 == paired.ModelSha256, token)
                ?? throw new InvalidDataException("验证模型资产缺失。");
            if (model is not null && (model.InputContract != RecipeModelInput.PairedGrayAbsDiff640V1 || model.ByteLength != model.Content.Length))
                throw new InvalidDataException("验证模型资产声明不符。");
            using var onnx = model is null ? null : new OnnxDetector(model.Content, paired!.ModelSha256);
            var thresholds = paired is null ? null : new OnnxScoreThresholds(paired.Thresholds.Open, paired.Thresholds.Short,
                paired.Thresholds.Mousebite, paired.Thresholds.Spur, paired.Thresholds.Copper, paired.Thresholds.PinHole);
            ClassicalDetector? classical = null;
            if (definition is ClassicalRecipeDefinition { Settings: var dto })
                classical = new ClassicalDetector(new ClassicalSettings(dto.BinarizationThreshold, dto.EdgeTolerance,
                    dto.MinimumArea, dto.ClosingSize, dto.BoxPadding, dto.MaximumTranslation, dto.MinimumAlignmentResponse));
            if (onnx is null && classical is null) throw new InvalidDataException("验证算法不支持。");
            var rows = new List<RecipeValidationRow>(200);
            var scores = new List<RecipeScore>(200);
            var sampleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                token.ThrowIfCancellationRequested();
                var sampleId = input.RootElement.GetProperty("sampleId").GetString()!;
                if (!sampleIds.Add(sampleId)) throw new InvalidDataException($"重复输入样本：{sampleId}");
                if (!truths.TryGetValue(sampleId, out var truth))
                    throw new InvalidDataException($"缺少真值行：{sampleId}");
                var truthBoxes = truth.RootElement.GetProperty("defects").EnumerateArray()
                    .Select(x => new RecipeTruth(x.GetProperty("box").EnumerateArray().Select(n => n.GetDouble()).ToArray(),
                        x.GetProperty("classId").GetInt32())).ToArray();
                if (truthBoxes.Any(x => x.ClassId is < 1 or > 6)) throw new InvalidDataException("验证真值类别不在 1–6 范围。");
                RecipeValidationRow row;
                RecipeScore score;
                try
                {
                    var tested = await ReadAndCheck(input.RootElement, "image", "imageSha256", dataRoot, token);
                    var reference = await ReadAndCheck(input.RootElement, "reference", "referenceSha256", dataRoot, token);
                    var result = onnx is null ? classical!.Detect(tested, reference, token)
                        : onnx.Detect(tested, reference, thresholds!, token);
                    var predictions = result.Defects.Select(x => new RecipePrediction(x.Box, x.ClassId, x.Score, x.Area)).ToArray();
                    score = RecipeScoring.Match(predictions, truthBoxes, paired is not null);
                    row = new(sampleId, "Completed", result.Decision, predictions, null, result.ElapsedMs, score.Tp, score.Fp, score.Fn);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or OpenCvSharp.OpenCVException or ArgumentException)
                {
                    row = new(sampleId, "Failed", "NotEvaluated", [], error.Message, null, 0, 0, truthBoxes.Length);
                    score = RecipeScoring.Match([], truthBoxes, paired is not null);
                }
                rows.Add(row);
                scores.Add(score);
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
                "首个成功检测样本单列冷启动；p50/p95 为其余成功样本的双图解码、检查、预处理、检测和后处理耗时；不含文件读取/hash及模型加载。失败图不计耗时分位，但计入 200 张人口及 FN。",
                paired is null ? "Localization" : "ClassAware",
                paired is null ? [] : Enumerable.Range(1, 6).Select(id => RecipeScoring.Metrics(id,
                    scores.Sum(x => x.Classes[id - 1].Tp), scores.Sum(x => x.Classes[id - 1].Fp), scores.Sum(x => x.Classes[id - 1].Fn))).ToArray(),
                onnx?.SessionInitializationMs, onnx?.ModelSha256);
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

    private static double Percentile(double[] values, double q)
    {
        if (values.Length == 0) return 0;
        var index = (values.Length - 1) * q;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return values[lower] + (values[upper] - values[lower]) * (index - lower);
    }
}
