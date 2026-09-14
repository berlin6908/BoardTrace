using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using CommunityToolkit.Mvvm.Input;

namespace BoardTrace.Station;

public sealed record BatchChoice(BatchSummary Summary)
{
    public string Label => $"{Summary.Batch.BatchNumber} · {Summary.Batch.PlannedQuantity} 件";
}

public sealed partial class StationViewModel
{
    private readonly HttpClient batchDeviceClient;
    private StationBatchClient? batchClient;
    private CachedBatchState? activeBatch;
    private StoredInspection? passedFirstArticle;
    private BatchChoice? selectedBatch;
    private string batchNotice = "刷新本工位批次，下载完整方案后执行首件。";
    private readonly SemaphoreSlim batchRefresh = new(1, 1);

    public ObservableCollection<BatchChoice> AssignedBatches { get; } = [];
    public AsyncRelayCommand RefreshBatchesCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadBatchCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshActiveBatchCommand { get; private set; } = null!;
    public AsyncRelayCommand StartBatchCommand { get; private set; } = null!;
    public BatchChoice? SelectedBatch
    {
        get => selectedBatch;
        set { if (SetProperty(ref selectedBatch, value)) NotifyBatchCommands(); }
    }
    public string BatchNotice { get => batchNotice; private set => SetProperty(ref batchNotice, value); }
    public string ActiveBatchNumber => activeBatch?.Batch.BatchNumber ?? "未选择批次";
    public string BatchProgress => activeBatch is null ? "工程回放不计入批次数量"
        : $"本地已接生产件 {activeBatch.AcceptedProductionCount} / {activeBatch.Batch.PlannedQuantity} · 首件、复检另计";
    public string InspectionMode => IsReinspectionMode ? "返工复检 · 模拟输入" : activeBatch is null ? "工程回放 · 模拟输入" : "批次执行 · 模拟输入";
    public string RunButtonText => IsReinspectionMode ? "执行返工复检" : activeBatch is null ? "开始检测" : activeBatch.Status == BatchStatus.AwaitingFirstArticle ? "执行首件检测" : "检测生产件";
    public string BatchStateText
    {
        get
        {
            if (activeBatch is null) return "当前为工程回放";
            if (activeBatch.Status == BatchStatus.Closed) return "中央已关闭批次 · 可下载下一批";
            if (activeRework?.InspectionId is not null) return "本次复检已接件 · 不能重复执行";
            if (activeBatch.Status == BatchStatus.AwaitingFirstArticle)
                return passedFirstArticle is null ? "等待首件检测"
                    : passedFirstArticle.AcknowledgedAt is null ? "首件已通过 · 等待中央接收" : "首件已上传 · 等待质量批准";
            if (!IsReinspectionMode && activeBatch.AcceptedProductionCount >= activeBatch.Batch.PlannedQuantity) return "计划数量已接收完毕 · 有票可复检";
            if (activeBatch.Session is not { } session) return "首件已批准 · 等待本班在线启动";
            if (session.ExpiresAt <= DateTimeOffset.UtcNow) return "生产授权已到期 · 请重新登录";
            if (session.OperatorId != currentOperator?.Id) return "等待当前操作员在线启动";
            return $"{(IsReinspectionMode ? "复检可执行" : "批次可生产")} · 授权至 {session.ExpiresAt.LocalDateTime:HH:mm}";
        }
    }

    private bool IsBatchBusy => RefreshBatchesCommand.IsRunning || DownloadBatchCommand.IsRunning || RefreshActiveBatchCommand.IsRunning || StartBatchCommand.IsRunning;
    private bool CanChangeRecipe => CanEdit && !IsReinspectionMode && activeBatch?.Status != BatchStatus.InProgress;
    private bool CanRunInspection => IsReinspectionMode ? CanContinueReinspection() : activeBatch is null ||
        (activeBatch.Status == BatchStatus.AwaitingFirstArticle && passedFirstArticle is null) || CanContinueProduction();

    private bool HasCurrentBatchSession() => activeBatch is { Status: BatchStatus.InProgress, Approval: not null, Session: { } session }
        && session.OperatorId == currentOperator?.Id && session.IssuedAt <= DateTimeOffset.UtcNow && session.ExpiresAt > DateTimeOffset.UtcNow
        && session.ArchiveId == activeBatch.ArchiveId;

    private bool CanContinueProduction() => !IsReinspectionMode && HasCurrentBatchSession()
        && activeBatch!.AcceptedProductionCount < activeBatch.Batch.PlannedQuantity;

    private void InitializeBatchCommands()
    {
        RefreshBatchesCommand = new AsyncRelayCommand(RefreshBatchesAsync, () => CanEdit && batchClient != null);
        DownloadBatchCommand = new AsyncRelayCommand(DownloadBatchAsync, () => CanEdit && batchClient != null && SelectedBatch != null
            && !coordinator.IsFaulted && (activeBatch is null || activeBatch.Status == BatchStatus.Closed || activeBatch.Batch.Id == SelectedBatch.Summary.Batch.Id));
        RefreshActiveBatchCommand = new AsyncRelayCommand(RefreshActiveBatchAsync, () => CanEdit && batchClient != null && activeBatch != null && !coordinator.IsFaulted);
        StartBatchCommand = new AsyncRelayCommand(StartBatchAsync, () => CanEdit && !coordinator.IsFaulted && activeBatch is { Approval: not null }
            && activeBatch.Status is BatchStatus.Approved or BatchStatus.InProgress && !HasCurrentBatchSession()
            && (activeBatch.AcceptedProductionCount < activeBatch.Batch.PlannedQuantity || ReworkOrders.Count > 0));
        foreach (var command in new[] { RefreshBatchesCommand, DownloadBatchCommand, RefreshActiveBatchCommand, StartBatchCommand })
            command.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(AsyncRelayCommand.IsRunning)) NotifyAccessChanged();
            };
    }

    private void NotifyBatchCommands()
    {
        RefreshBatchesCommand.NotifyCanExecuteChanged();
        DownloadBatchCommand.NotifyCanExecuteChanged();
        RefreshActiveBatchCommand.NotifyCanExecuteChanged();
        StartBatchCommand.NotifyCanExecuteChanged();
    }

    private async Task InitializeCachedBatchAsync()
    {
        var cached = await Task.Run(store.ReadActiveBatch);
        if (cached is null) return;
        var recipe = await Task.Run(() => recipeStore.Load(cached.Batch.RecipeVersionId));
        coordinator.RestoreCachedBatchRecipe(recipe);
        loadedRecipe = recipe;
        var allowed = recipe.SampleIds.ToHashSet(StringComparer.Ordinal);
        SelectRecipeSamples(replaySamples.Where(sample => allowed.Contains(sample.SampleId)).ToArray());
        await RefreshBatchStateAsync();
        RecipeNotice = "已恢复当前批次固定缓存方案。";
    }

    private async Task RefreshBatchStateAsync()
    {
        await batchRefresh.WaitAsync();
        try
        {
            var state = await Task.Run(() =>
            {
                var batch = store.ReadActiveBatch();
                return (Batch: batch, Pass: batch is null ? null : store.ReadPassedFirstArticle(batch.Batch.Id),
                    Rework: store.ReadSelectedReworkOrder(), Pending: batch is null ? Array.Empty<ReworkOrder>() : store.ReadPendingReworkOrders(batch.Batch.Id));
            });
            activeBatch = state.Batch;
            passedFirstArticle = state.Pass;
            UpdateReworkDisplay(state.Rework, state.Pending);
            UpdateBatchDisplay();
        }
        finally { batchRefresh.Release(); }
    }

    private void UpdateBatchDisplay()
    {
        foreach (var property in new[] { nameof(ActiveBatchNumber), nameof(BatchProgress), nameof(BatchStateText), nameof(InspectionMode), nameof(RunButtonText) })
            OnPropertyChanged(property);
        NotifyAccessChanged();
    }

    private async Task RefreshBatchesAsync()
    {
        BatchNotice = "正在读取本工位批次…";
        try
        {
            var batches = await batchClient!.GetAssignedAsync(uploadCancellation.Token);
            var selectedId = SelectedBatch?.Summary.Batch.Id ?? activeBatch?.Batch.Id;
            AssignedBatches.Clear();
            foreach (var batch in batches.Where(batch => batch.Status != BatchStatus.Closed)) AssignedBatches.Add(new BatchChoice(batch));
            SelectedBatch = AssignedBatches.FirstOrDefault(batch => batch.Summary.Batch.Id == selectedId) ?? AssignedBatches.FirstOrDefault();
            BatchNotice = AssignedBatches.Count == 0 ? "本工位暂无未关闭批次，请由工艺人员下发。" : "选择批次并下载；已有批次可刷新首件批准。";
        }
        catch (Exception error) { BatchOperationFailed("刷新批次失败", error, personnel: false); }
    }

    private Task DownloadBatchAsync() => DownloadAndApplyBatchAsync(SelectedBatch!.Summary.Batch.Id);
    private Task RefreshActiveBatchAsync() => DownloadAndApplyBatchAsync(activeBatch!.Batch.Id);

    private async Task DownloadAndApplyBatchAsync(Guid batchId)
    {
        BatchNotice = "正在下载批次、核对方案与参考图…";
        try
        {
            var downloaded = await batchClient!.DownloadAsync(batchId, recipeStore, uploadCancellation.Token);
            await ApplyBatchAsync(downloaded.Package, downloaded.Recipe);
            await UpdateRecipeChoicesAsync();
        }
        catch (Exception error) { BatchOperationFailed("批次下载或批准刷新失败", error, personnel: false); }
    }

    private async Task ApplyBatchAsync(BatchPackage package, LoadedClassicalRecipe recipe)
    {
        if (package.Batch.StationId != StationId) throw new InvalidDataException("该批次不属于当前工位。");
        var allowed = recipe.SampleIds.ToHashSet(StringComparer.Ordinal);
        var samples = replaySamples.Where(sample => allowed.Contains(sample.SampleId)).ToArray();
        if (samples.Length == 0) throw new InvalidDataException("批次方案与工位回放清单没有共同样本。");
        coordinator.UseBatch(package, recipe);
        loadedRecipe = recipe;
        SelectRecipeSamples(samples);
        await RefreshBatchStateAsync();
        BatchNotice = BatchStateText;
        RecipeNotice = "批次绑定完整缓存方案，版本与参考资产已固定。";
        if (activeBatch?.Status == BatchStatus.Closed)
        {
            BatchNotice = "中央已关闭此批次，人员授权已结束。刷新工位批次后可下载下一批，旧档案继续保留。";
            Status = "中央已关闭批次";
            Notice = BatchNotice;
        }
        else
        {
            Status = BatchStateText;
            Notice = $"已加载批次 {ActiveBatchNumber}，检测使用已核对的固定方案与参考图。";
        }
        AddEvent($"批次 {ActiveBatchNumber} 已加载 · {BatchStateText}。");
    }

    private async Task StartBatchAsync()
    {
        var cached = activeBatch!;
        var batch = cached.Batch;
        BatchNotice = "正在为当前操作员申请批次启动…";
        try
        {
            using var response = await operatorClient!.PostAsJsonAsync($"api/batches/{batch.Id}/execution-sessions",
                new StartBatchRequest(StationId, batch.RecipeBundleHash, cached.ArchiveId), uploadCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                var message = await response.Content.ReadAsStringAsync(uploadCancellation.Token);
                if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
                {
                    using var problem = JsonDocument.Parse(message);
                    if (problem.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                        message = title.GetString()!;
                }
                throw new HttpRequestException(string.IsNullOrWhiteSpace(message) ? "中央未允许启动批次。" : message, null, response.StatusCode);
            }
            var session = await response.Content.ReadFromJsonAsync<BatchExecutionSession>(uploadCancellation.Token)
                ?? throw new InvalidDataException("中央未返回生产授权。");
            if (session.OperatorId != currentOperator!.Id) throw new UnauthorizedAccessException("启动授权未对应当前操作员，请重新登录。");
            if (session.ArchiveId != cached.ArchiveId)
                throw new InvalidDataException("中央授权不属于当前本地批次档案，请恢复原工位数据库。");
            var protectedResume = OfflineBatchResume.Protect(options, currentOperator, personnelSession!, session);
            coordinator.StartBatch(session, protectedResume);
            await RefreshBatchStateAsync();
            BatchNotice = BatchStateText;
            AddEvent($"操作员 {OperatorName} 在线启动批次 {ActiveBatchNumber}。");
        }
        catch (Exception error) { BatchOperationFailed("启动批次失败", error, personnel: true); }
    }

    private void BatchOperationFailed(string action, Exception error, bool personnel)
    {
        if (stopping && error is OperationCanceledException) return;
        if (personnel && (error is UnauthorizedAccessException || error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }))
        {
            sessionExpired = true;
            ClearLocalOperatorSession();
            BatchNotice = "人员登录已失效，请退出 / 换班后重新登录。";
        }
        else BatchNotice = $"{action}：{error.Message}";
        AddEvent(BatchNotice);
        NotifyAccessChanged();
    }

    private bool ClearLocalOperatorSession()
    {
        try { coordinator.EndOperatorSession(); return true; }
        catch (Exception error)
        {
            Status = "本地会话清除失败 · 已停止接件";
            Notice = "检查本地存储后重启工位。" + error.Message;
            AddEvent(Notice);
            return false;
        }
    }

    private async Task<CurrentUser?> AuthorizeInspectionAsync(InspectionPurpose purpose)
    {
        try
        {
            var actor = await StationAuthentication.CurrentOperatorAsync(operatorClient!, uploadCancellation.Token);
            if (actor.Id != currentOperator!.Id) throw new UnauthorizedAccessException("人员会话已改变，请退出 / 换班后重新登录。");
            currentOperator = actor;
            OnPropertyChanged(nameof(OperatorName));
            return actor;
        }
        catch (UnauthorizedAccessException error)
        {
            sessionExpired = true;
            if (ClearLocalOperatorSession())
            {
                Status = "登录已失效 · 未接受检测";
                Notice = error.Message + " 请点击退出 / 换班重新登录。";
            }
            await RefreshBatchStateAsync();
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            var unavailable = error is HttpRequestException { StatusCode: null } or OperationCanceledException
                || error is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError };
            if (!stopping && !signingOut && unavailable &&
                ((purpose == InspectionPurpose.Production && CanContinueProduction()) || (purpose == InspectionPurpose.Reinspection && CanContinueReinspection())))
            {
                BatchNotice = "中央暂不可达，按当前批次与未过期人员授权继续；原件保留并等待补传。";
                return currentOperator;
            }
            Status = "无法验证人员登录 · 未接受检测";
            Notice = purpose is InspectionPurpose.Production or InspectionPurpose.Reinspection ? "请检查中央连接与批次启动授权后重试。" : "工程回放和首件需要在线验证人员身份，请恢复连接后重试。";
            return null;
        }
    }
}
