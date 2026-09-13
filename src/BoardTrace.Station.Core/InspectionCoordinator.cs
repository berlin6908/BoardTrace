using BoardTrace.Contracts;
using BoardTrace.Vision;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace BoardTrace.Station.Core;

public sealed class InspectionCoordinator
{
    private readonly LocalInspectionStore store;
    private readonly ClassicalDetector detector;
    private readonly string recipeJson;
    private readonly string recipeId;
    private int busy;
    private volatile bool faulted;

    public InspectionCoordinator(LocalInspectionStore store, ClassicalSettings settings)
    {
        this.store = store;
        detector = new ClassicalDetector(settings);
        recipeJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        recipeId = "classical-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recipeJson)));
    }

    public bool IsFaulted => faulted;

    public async Task<InspectionRecord> InspectAsync(string stationId, string productId, CurrentUser operatorUser, IImageSource source,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        var actor = StationAuthentication.RequireOperator(operatorUser);
        var operatorId = actor.Id;
        var operatorName = actor.DisplayName;
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("工位忙，未接受新的检测。");
        try
        {
            if (faulted) throw new InvalidOperationException("本地保存失败，工位已停止接件；检查存储后重启工位。");
            return await Task.Run(async () =>
            {
                var record = new InspectionRecord
                {
                    Id = Guid.NewGuid(), StationId = stationId, ProductId = productId,
                    OperatorId = operatorId, OperatorName = operatorName,
                    SampleId = source.SampleId, SourceKind = source.SourceKind,
                    RecipeId = recipeId, RecipeJson = recipeJson, StartedAt = DateTimeOffset.UtcNow
                };
                store.Begin(record);
                try
                {
                    progress?.Report("正在采集图像");
                    var pair = await source.CaptureAsync(cancellationToken);
                    record = record with { TestedImage = pair.Tested, ReferenceImage = pair.Reference };
                    progress?.Report("正在检测");
                    var result = detector.Detect(pair.Tested, pair.Reference, cancellationToken);
                    record = record with
                    {
                        ExecutionStatus = InspectionExecution.Completed,
                        Decision = Enum.Parse<QualityDecision>(result.Decision),
                        Width = result.Width, Height = result.Height, DetectionMs = result.ElapsedMs,
                        Defects = result.Defects.Select(d => new DefectBox(d.Box, d.ClassId, d.Score, d.Area)).ToArray(),
                        Diagnostics = result.Diagnostics
                    };
                }
                catch (Exception error) when (error is IOException or InvalidDataException or OpenCVException or
                                               ArgumentException or OperationCanceledException)
                {
                    record = record with
                    {
                        ExecutionStatus = error is OperationCanceledException ? InspectionExecution.Interrupted : InspectionExecution.Failed,
                        Decision = QualityDecision.NotEvaluated,
                        Error = error is OperationCanceledException ? "操作已取消，未完成质量判定。" : error.Message
                    };
                }
                record = record with { CompletedAt = DateTimeOffset.UtcNow };
                progress?.Report("正在保存本地档案");
                store.Complete(record);
                // A valid result can leave this method only after the BLOB/result/outbox commit.
                return record;
            });
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
}
