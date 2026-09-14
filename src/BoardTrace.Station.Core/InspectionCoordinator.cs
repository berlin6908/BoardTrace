using BoardTrace.Contracts;
using BoardTrace.Vision;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace BoardTrace.Station.Core;

public sealed class InspectionCoordinator : IDisposable
{
    private readonly LocalInspectionStore store;
    private ClassicalDetector? developmentDetector;
    private LoadedRecipe? publishedRecipe;
    private string recipeJson = "";
    private string recipeId = "";
    private int busy;
    private volatile bool faulted;
    private bool disposed;

    public InspectionCoordinator(LocalInspectionStore store, ClassicalSettings settings)
    {
        this.store = store;
        SetDevelopmentRecipe(settings);
    }

    private void SetDevelopmentRecipe(ClassicalSettings settings, bool leaveBatch = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var nextDetector = new ClassicalDetector(settings);
        var nextJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var nextId = "classical-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nextJson)));
        if (leaveBatch) store.LeaveBatch();
        var previous = publishedRecipe;
        developmentDetector = nextDetector;
        publishedRecipe = null;
        recipeJson = nextJson;
        recipeId = nextId;
        previous?.Dispose();
    }

    public void UseDevelopmentRecipe(ClassicalSettings settings) => ChangeWhileIdle(() => SetDevelopmentRecipe(settings, leaveBatch: true));

    public void UsePublishedRecipe(LoadedRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ChangeWhileIdle(() =>
        {
            recipe.ThrowIfDisposed();
            store.LeaveBatch();
            SetPublishedRecipe(recipe);
        });
    }

    private void SetPublishedRecipe(LoadedRecipe recipe)
    {
        var previous = publishedRecipe;
        publishedRecipe = recipe;
        developmentDetector = null;
        recipeId = recipe.VersionId.ToString("D");
        recipeJson = recipe.RecipeJson;
        if (!ReferenceEquals(previous, recipe)) previous?.Dispose();
    }

    public void UseBatch(BatchPackage package, LoadedRecipe recipe) => ChangeWhileIdle(() =>
    {
        recipe.ThrowIfDisposed();
        store.SelectBatch(package, recipe);
        SetPublishedRecipe(recipe);
    });

    // Startup reloads the already selected immutable recipe while retaining a PLC result.
    // This does not select a batch or change its persisted session/approval/quantity.
    public void RestoreCachedBatchRecipe(LoadedRecipe recipe) => ChangeWhileIdle(() =>
    {
        recipe.ThrowIfDisposed();
        var active = store.ReadActiveBatch() ?? throw new InspectionRejectedException("本地尚未选择批次。");
        if (active.Batch.RecipeVersionId != recipe.VersionId || active.Batch.RecipeBundleHash != recipe.BundleHash)
            throw new InspectionRejectedException("恢复方案与当前缓存批次的固定版本不一致。");
        SetPublishedRecipe(recipe);
    });

    public void StartBatch(BatchExecutionSession session, byte[]? resumePayload) =>
        ChangeWhileIdle(() => store.SaveExecutionSession(session, resumePayload));

    public void UseReworkOrder(Guid orderId) => ChangeWhileIdle(() => store.SelectReworkOrder(orderId));
    public void ExitReinspection() => ChangeWhileIdle(store.ExitReinspection);

    public void EndOperatorSession()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InspectionRejectedException("请等待已接受检测完成后退出人员会话。");
        try { ObjectDisposedException.ThrowIf(disposed, this); store.ClearExecutionSession(); }
        catch
        {
            faulted = true;
            throw;
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    private void ChangeWhileIdle(Action change)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("工位忙，不能变更检测状态。");
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (faulted) throw new InvalidOperationException("本地保存失败，工位已停止接件，不能通过切换方案恢复。");
            change();
        }
        catch (InspectionRejectedException) { throw; }
        catch { faulted = true; throw; }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    public bool IsFaulted => faulted;
    public bool IsBusy => Volatile.Read(ref busy) != 0;

    public void ConfirmPlcAck(Guid inspectionId) => ChangeWhileIdle(() => store.ConfirmPlcAck(inspectionId));

    public int RecoverInterrupted()
    {
        var recovered = 0;
        ChangeWhileIdle(() => recovered = store.RecoverInterrupted());
        return recovered;
    }

    public async Task<InspectionRecord> InspectAsync(InspectionPurpose purpose, string stationId, string productId, CurrentUser operatorUser, IImageSource source,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        InspectionIdentity? identity = null, Action<InspectionRecord>? startedCommitted = null)
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
            ObjectDisposedException.ThrowIf(disposed, this);
            if (faulted) throw new InvalidOperationException("本地保存失败，工位已停止接件；检查存储后重启工位。");
            return await Task.Run(async () =>
            {
                var record = new InspectionRecord
                {
                    Id = Guid.NewGuid(), StationId = stationId, ProductId = productId, Purpose = purpose,
                    ControllerSessionId = identity?.ControllerSessionId, TriggerSequence = identity?.TriggerSequence,
                    OperatorId = operatorId, OperatorName = operatorName,
                    SampleId = source.SampleId, SourceKind = source.SourceKind,
                    ReworkOrderId = purpose == InspectionPurpose.Reinspection ? store.ReadSelectedReworkOrder()?.Order.Id : null,
                    RecipeId = recipeId, RecipeJson = recipeJson, StartedAt = DateTimeOffset.UtcNow
                };
                var accepted = store.BeginAccepted(record, publishedRecipe?.BundleHash);
                record = accepted.Record;
                if (!accepted.IsNew) return record;
                try
                {
                    startedCommitted?.Invoke(record);
                    progress?.Report("正在采集图像");
                    var pair = await source.CaptureAsync(cancellationToken);
                    record = record with { TestedImage = pair.Tested };
                    var reference = publishedRecipe is not null
                        ? publishedRecipe.GetReferenceBytes(source.SampleId)
                        : pair.Reference ?? throw new InvalidDataException("工程回放必须提供参考图，当前未加载已发布方案。");
                    record = record with { ReferenceImage = reference };
                    progress?.Report("正在检测");
                    var result = publishedRecipe is not null
                        ? publishedRecipe.Detect(source.SampleId, pair.Tested, cancellationToken)
                        : developmentDetector!.Detect(pair.Tested, reference, cancellationToken);
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
        catch (InspectionRejectedException) { throw; }
        catch
        {
            faulted = true;
            throw;
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("请等待已接受检测完成后释放工位方案。");
        try
        {
            if (disposed) return;
            disposed = true;
            publishedRecipe?.Dispose();
            publishedRecipe = null;
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
}
