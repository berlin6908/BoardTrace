using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BoardTrace.Station;

public sealed record DefectOverlay(double Left, double Top, double Width, double Height);
public sealed record InspectionRow(StoredInspection Stored)
{
    public Guid Id => Stored.Record.Id;
    public string Time => Stored.Record.StartedAt.ToLocalTime().ToString("HH:mm:ss");
    public string Product => Stored.Record.ProductId;
    public string Sample => Stored.Record.SampleId;
    public string Execution => Stored.Record.ExecutionStatus switch
    {
        InspectionExecution.Completed => "已完成", InspectionExecution.Failed => "执行失败",
        InspectionExecution.Interrupted => "已中断", _ => "未完成"
    };
    public string Decision => StationViewModel.DecisionLabel(Stored.Record.Decision);
    public int Defects => Stored.Record.Defects.Count;
    public string Upload => Stored.PendingUpload ? "待上传" : Stored.AcknowledgedAt is not null ? "已确认" : "未生成结果";
    public string Purpose => Stored.Record.Purpose switch
    {
        InspectionPurpose.FirstArticle => "首件", InspectionPurpose.Production => $"生产 #{Stored.Record.ProductionSequence}", _ => "工程回放"
    };
}

public sealed partial class StationViewModel : ObservableObject, IAsyncDisposable
{
    private readonly StationOptions options;
    private readonly LocalInspectionStore store;
    private readonly InspectionCoordinator coordinator;
    private readonly HttpClient uploadClient;
    private InspectionUploader? uploader;
    private HttpClient? operatorClient;
    private CurrentUser? currentOperator;
    private bool signingOut;
    private bool sessionExpired;
    private readonly CancellationTokenSource uploadCancellation = new();
    private readonly SemaphoreSlim historyRefresh = new(1, 1);
    private Task? uploadTask;
    private Task? initializationTask;
    private Task? shutdownTask;
    private bool stopping;
    private bool ready;
    private ReplaySample? selectedSample;
    private string productId = $"SIM-{DateTime.Now:yyyyMMdd-HHmmss}";
    private bool constructedNormal;
    private string status = "正在载入工位";
    private string notice = "正在读取回放清单与本地档案…";
    private InspectionRecord? current;
    private BitmapSource? testedImage;
    private BitmapSource? referenceImage;
    private int pendingCount;
    private InspectionRow? selectedHistory;
    private string uploadStatus = "等待同步";
    private string uploadNotice = "检测完成后自动上传。";

    public StationViewModel(StationOptions options, CurrentUser user, HttpClient operatorClient)
    {
        this.options = options;
        currentOperator = StationAuthentication.RequireOperator(user);
        this.operatorClient = operatorClient;
        store = new LocalInspectionStore(options.DatabasePath);
        coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        uploadClient = StationAuthentication.CreateClient(options.ServerUrl);
        batchDeviceClient = StationAuthentication.CreateClient(options.ServerUrl);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanEdit && !plcRunning && CanRunInspection && !coordinator.IsFaulted && SelectedSample != null && !string.IsNullOrWhiteSpace(ProductId));
        ViewHistoryCommand = new AsyncRelayCommand(ViewHistoryAsync, () => SelectedHistory != null && CanEdit);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync, () => currentOperator != null && !stopping && !signingOut);
        InitializeRecipeCommands();
        InitializeBatchCommands();
        InitializePlcCommands();
        RunCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RunCommand.IsRunning)) return;
            OnPropertyChanged(nameof(CanEdit));
            ViewHistoryCommand.NotifyCanExecuteChanged();
            NotifyRecipeCommands();
            NotifyBatchCommands();
        };
        ViewHistoryCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewHistoryCommand.IsRunning)) return;
            OnPropertyChanged(nameof(CanEdit));
            RunCommand.NotifyCanExecuteChanged();
            NotifyRecipeCommands();
            NotifyBatchCommands();
        };
    }

    public string StationId => options.StationId;
    public string DatabasePath => options.DatabasePath;
    public string ServerAddress => options.ServerUrl.ToString();
    public string OperatorName => currentOperator?.DisplayName ?? "未登录";
    public bool CanEdit => !stopping && !signingOut && !sessionExpired && currentOperator != null && ready && !plcInspecting && !plcWaitingAck && !RunCommand.IsRunning && !ViewHistoryCommand.IsRunning && !IsRecipeBusy && !IsBatchBusy;
    public ObservableCollection<ReplaySample> Samples { get; } = [];
    public ObservableCollection<InspectionRow> History { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    public ObservableCollection<DefectOverlay> DefectOverlays { get; } = [];
    public AsyncRelayCommand RunCommand { get; }
    public AsyncRelayCommand ViewHistoryCommand { get; }
    public AsyncRelayCommand SignOutCommand { get; }
    public event EventHandler? LoginRequested;

    public ReplaySample? SelectedSample
    {
        get => selectedSample;
        set { if (SetProperty(ref selectedSample, value)) RunCommand.NotifyCanExecuteChanged(); }
    }
    public string ProductId
    {
        get => productId;
        set { if (SetProperty(ref productId, value)) RunCommand.NotifyCanExecuteChanged(); }
    }
    public bool ConstructedNormal { get => constructedNormal; set => SetProperty(ref constructedNormal, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string Notice { get => notice; private set => SetProperty(ref notice, value); }
    public int PendingCount { get => pendingCount; private set => SetProperty(ref pendingCount, value); }
    public string UploadStatus { get => uploadStatus; private set => SetProperty(ref uploadStatus, value); }
    public string UploadNotice { get => uploadNotice; private set => SetProperty(ref uploadNotice, value); }
    public BitmapSource? TestedImage { get => testedImage; private set => SetProperty(ref testedImage, value); }
    public BitmapSource? ReferenceImage { get => referenceImage; private set => SetProperty(ref referenceImage, value); }
    public InspectionRow? SelectedHistory
    {
        get => selectedHistory;
        set { if (SetProperty(ref selectedHistory, value)) ViewHistoryCommand.NotifyCanExecuteChanged(); }
    }
    public string Decision => DecisionLabel(current?.Decision ?? QualityDecision.NotEvaluated);
    public Brush DecisionBrush => current?.Decision switch
    {
        QualityDecision.Pass => Brushes.SeaGreen, QualityDecision.Fail => Brushes.IndianRed, _ => Brushes.SlateGray
    };
    public string DefectCount => current?.ExecutionStatus == InspectionExecution.Completed ? current.Defects.Count.ToString() : "—";
    public string DetectionTime => current?.DetectionMs is double ms ? $"{ms:F1} ms" : "—";
    public string ResultCaption => current is null ? "尚未选择检测档案" : $"{current.ProductId} · {(current.Purpose == InspectionPurpose.FirstArticle ? "首件" : current.Purpose == InspectionPurpose.Production ? $"生产 #{current.ProductionSequence}" : "工程回放")} · 样本 {current.SampleId} · {(current.SourceKind == "ConstructedNormal" ? "构造正常输入" : "数据集回放")} · 操作员 {current.OperatorName}";
    public string InspectionId => current?.Id.ToString() ?? "等待检测";

    public static string DecisionLabel(QualityDecision decision) => decision switch
    {
        QualityDecision.Pass => "合格", QualityDecision.Fail => "缺陷", _ => "未判定"
    };

    public Task InitializeAsync() => initializationTask ??= stopping ? Task.CompletedTask : InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            await Task.Run(store.Initialize);
            var recovered = await Task.Run(coordinator.RecoverInterrupted);
            if (recovered > 0) AddEvent($"已恢复 {recovered} 条中断档案，未重新采图。");
            var lines = await File.ReadAllLinesAsync(options.ManifestPath);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
                replaySamples.Add(JsonSerializer.Deserialize<ReplaySample>(line, json) ?? throw new InvalidDataException("回放清单包含空记录。"));
            foreach (var sample in replaySamples) Samples.Add(sample);
            SelectedSample = Samples.FirstOrDefault();
            await InitializeRecipesAsync();
            await InitializeCachedBatchAsync();
            var pendingPlc = await Task.Run(store.ReadUnacknowledgedPlc);
            if (pendingPlc is not null)
            {
                plcWaitingAck = pendingPlc.Record.ExecutionStatus != InspectionExecution.Started;
                ShowRecord(await Task.Run(() => store.Get(pendingPlc.Record.Id)));
                PlcStatus = "已恢复未确认检测，请连接 PLC 完成原件握手";
            }
            await RefreshHistoryAsync();
            if (stopping) return;
            try
            {
                var credentials = JsonSerializer.Deserialize<StationCredentials>(await File.ReadAllTextAsync(options.CredentialsPath), json);
                if (credentials is null || string.IsNullOrWhiteSpace(credentials.UserName) || string.IsNullOrWhiteSpace(credentials.Password))
                    throw new InvalidDataException("设备账号和密码不能为空。");
                uploader = new InspectionUploader(store, uploadClient, credentials);
                batchClient = new StationBatchClient(batchDeviceClient, credentials, StationId);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                UploadStatus = "设备凭据不可用";
                UploadNotice = $"无法读取有效的设备账号。修正 {options.CredentialsPath} 后重启工位；待上传原件保留。";
                AddEvent(UploadNotice);
            }
            if (stopping) return;
            ready = true;
            Status = "准备就绪";
            Notice = activeBatch is null ? "选择输入并开始检测。当前为工程回放，不计入生产批次。" : BatchStateText;
            AddEvent($"工位已启动，载入 {Samples.Count} 个回放输入。");
            if (uploader != null) uploadTask = UploadLoopAsync(uploadCancellation.Token);
        }
        catch (Exception error)
        {
            Status = "启动失败";
            Notice = error.Message;
            AddEvent("启动失败：" + error.Message);
        }
        OnPropertyChanged(nameof(CanEdit));
        RunCommand.NotifyCanExecuteChanged();
        NotifyRecipeCommands();
        NotifyBatchCommands();
        StartPlcCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync()
    {
        if (stopping || signingOut || sessionExpired || currentOperator is null || operatorClient is null) return;
        var sample = SelectedSample!;
        var product = ProductId.Trim();
        var source = CreateReplaySource(sample, ConstructedNormal);
        var purpose = activeBatch is null ? InspectionPurpose.EngineeringReplay
            : activeBatch.Status == BatchStatus.AwaitingFirstArticle ? InspectionPurpose.FirstArticle : InspectionPurpose.Production;
        SelectedHistory = null;
        ShowRecord(null);
        var actor = await AuthorizeInspectionAsync(purpose);
        if (actor is null || stopping || signingOut) return;
        Notice = "正在处理，完成本地保存后显示判定。";
        InspectionRecord result;
        try
        {
            result = await coordinator.InspectAsync(purpose, StationId, product, actor, source, new Progress<string>(stage => Status = stage));
        }
        catch (InspectionRejectedException error)
        {
            Status = "未接受检测";
            Notice = error.Message;
            await RefreshBatchStateAsync();
            return;
        }
        catch (Exception error)
        {
            ShowRecord(null);
            Status = "工位故障 · 已停止接件";
            Notice = "未发布有效判定。检查本地存储后重启工位。" + error.Message;
            AddEvent("工位停止：" + error.Message);
            RunCommand.NotifyCanExecuteChanged();
            return;
        }
        ShowRecord(result);
        Status = result.ExecutionStatus == InspectionExecution.Completed ? "检测完成 · 已保存" : "执行失败 · 已保存";
        Notice = result.Error ?? "原图、参考图、检测结果和待上传记录已一并保存。";
        AddEvent($"{product} · {Decision} · 本地提交成功。");
        try { await RefreshHistoryAsync(); }
        catch (Exception error) { Notice += " 档案列表刷新失败：" + error.Message; }
        ProductId = $"SIM-{DateTime.Now:yyyyMMdd-HHmmssfff}";
        RunCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshHistoryAsync()
    {
        await historyRefresh.WaitAsync();
        try
        {
            var rows = await Task.Run(() => store.ReadRecent());
            PendingCount = await Task.Run(store.PendingCount);
            var selectedId = SelectedHistory?.Id;
            History.Clear();
            foreach (var row in rows) History.Add(new InspectionRow(row));
            SelectedHistory = History.FirstOrDefault(row => row.Id == selectedId);
            await RefreshBatchStateAsync();
        }
        finally { historyRefresh.Release(); }
    }

    private async Task UploadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(5);
                try
                {
                    var result = await uploader!.UploadPendingAsync(cancellationToken: cancellationToken);
                    var previousNotice = UploadNotice;
                    switch (result.Connection)
                    {
                        case UploadConnection.Connected:
                            UploadStatus = "最近同步成功";
                            UploadNotice = $"{DateTime.Now:HH:mm:ss} 中央已确认，本地原件继续保留。";
                            if (result.Pending > 0) delay = TimeSpan.FromMilliseconds(200);
                            break;
                        case UploadConnection.Unavailable:
                            UploadStatus = "中央暂不可达";
                            UploadNotice = result.Error!;
                            break;
                        case UploadConnection.Rejected:
                            UploadStatus = "同步需处理";
                            UploadNotice = result.Error!;
                            break;
                    }
                    if (result.Uploaded > 0)
                    {
                        AddEvent($"中央已确认 {result.Uploaded} 条检测记录，待上传 {result.Pending} 条。");
                    }
                    else if (result.Error != null && previousNotice != UploadNotice) AddEvent(UploadNotice);
                    if (result.Uploaded > 0 || result.Pending != PendingCount) await RefreshHistoryAsync();
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    UploadStatus = "同步失败";
                    var nextNotice = "本地上传状态未确认：" + error.Message;
                    if (UploadNotice != nextNotice) AddEvent(nextNotice);
                    UploadNotice = nextNotice;
                }
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync() => new(shutdownTask ??= ShutdownAsync());

    public void SignIn(CurrentUser user, HttpClient client)
    {
        if (stopping || signingOut || currentOperator != null) throw new InvalidOperationException("工位当前不能切换登录。");
        currentOperator = StationAuthentication.RequireOperator(user);
        operatorClient = client;
        sessionExpired = false;
        Status = coordinator.IsFaulted ? "工位故障 · 已停止接件" : "准备就绪";
        Notice = coordinator.IsFaulted ? "检查本地存储后重启工位。" : activeBatch is null ? "人员已登录，可以开始工程回放。" : "人员已登录，请在线启动当前批次。";
        AddEvent($"操作员 {user.DisplayName} 已登录。");
        NotifyAccessChanged();
    }

    private async Task SignOutAsync()
    {
        signingOut = true;
        NotifyAccessChanged();
        Notice = "正在换班，等待已接受的操作保存完成…";
        if (initializationTask != null) await initializationTask;
        await AwaitPlcInspectionAsync();
        await Task.WhenAll(RunCommand.ExecutionTask ?? Task.CompletedTask, ViewHistoryCommand.ExecutionTask ?? Task.CompletedTask,
            RefreshRecipesCommand.ExecutionTask ?? Task.CompletedTask, DownloadRecipeCommand.ExecutionTask ?? Task.CompletedTask,
            LoadCachedRecipeCommand.ExecutionTask ?? Task.CompletedTask, RefreshBatchesCommand.ExecutionTask ?? Task.CompletedTask,
            DownloadBatchCommand.ExecutionTask ?? Task.CompletedTask, RefreshActiveBatchCommand.ExecutionTask ?? Task.CompletedTask,
            StartBatchCommand.ExecutionTask ?? Task.CompletedTask);
        var sessionCleared = ClearLocalOperatorSession();
        try { await RefreshBatchStateAsync(); }
        catch (Exception error)
        {
            sessionCleared = false;
            AddEvent("换班时读取本地批次失败：" + error.Message);
        }
        var actorName = currentOperator?.DisplayName;
        try
        {
            if (operatorClient != null)
            {
                using var response = await operatorClient.PostAsync("api/auth/logout", null);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            AddEvent("中央未确认注销；本机人员会话已清除，下次登录重新鉴别。");
        }
        finally
        {
            operatorClient?.Dispose();
            operatorClient = null;
            currentOperator = null;
            signingOut = false;
            sessionExpired = false;
            ShowRecord(null);
            if (!stopping)
            {
                Status = sessionCleared ? "人员已退出" : "本地会话清除失败 · 已停止接件";
                Notice = sessionCleared ? "等待下一位操作员登录，设备补传继续运行。" : "检查本地存储后重启工位，设备补传继续运行。";
            }
            AddEvent($"操作员 {actorName} 已退出，设备补传继续运行。");
            NotifyAccessChanged();
        }
        if (!stopping) LoginRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyAccessChanged()
    {
        OnPropertyChanged(nameof(OperatorName));
        OnPropertyChanged(nameof(CanEdit));
        RunCommand.NotifyCanExecuteChanged();
        ViewHistoryCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
        NotifyRecipeCommands();
        NotifyBatchCommands();
        OnPropertyChanged(nameof(BatchStateText));
    }

    private async Task ShutdownAsync()
    {
        stopping = true;
        NotifyAccessChanged();
        Notice = "正在关闭，等待已接受的操作保存完成…";
        try
        {
            await uploadCancellation.CancelAsync();
            await StopPlcAsync();
            if (initializationTask != null) await initializationTask;
            await Task.WhenAll(RunCommand.ExecutionTask ?? Task.CompletedTask, ViewHistoryCommand.ExecutionTask ?? Task.CompletedTask,
                SignOutCommand.ExecutionTask ?? Task.CompletedTask, RefreshRecipesCommand.ExecutionTask ?? Task.CompletedTask,
                DownloadRecipeCommand.ExecutionTask ?? Task.CompletedTask, LoadCachedRecipeCommand.ExecutionTask ?? Task.CompletedTask,
                RefreshBatchesCommand.ExecutionTask ?? Task.CompletedTask, DownloadBatchCommand.ExecutionTask ?? Task.CompletedTask,
                RefreshActiveBatchCommand.ExecutionTask ?? Task.CompletedTask, StartBatchCommand.ExecutionTask ?? Task.CompletedTask);
            if (uploadTask != null) await uploadTask;
        }
        finally
        {
            uploadClient.Dispose();
            batchDeviceClient.Dispose();
            operatorClient?.Dispose();
            uploadCancellation.Dispose();
            historyRefresh.Dispose();
        }
    }

    private async Task ViewHistoryAsync()
    {
        if (stopping || signingOut || sessionExpired || currentOperator is null) return;
        var id = SelectedHistory!.Id;
        try
        {
            var record = await Task.Run(() => store.Get(id));
            ShowRecord(record);
            Notice = record?.Error ?? "正在查看本地档案。历史判定保持原样。";
        }
        catch (Exception error) { Notice = "读取档案失败：" + error.Message; }
    }

    private void ShowRecord(InspectionRecord? record)
    {
        current = record;
        TestedImage = Decode(record?.TestedImage);
        ReferenceImage = Decode(record?.ReferenceImage);
        DefectOverlays.Clear();
        if (record != null)
            foreach (var defect in record.Defects)
                DefectOverlays.Add(new DefectOverlay(defect.Box[0], defect.Box[1], defect.Box[2] - defect.Box[0], defect.Box[3] - defect.Box[1]));
        foreach (var property in new[] { nameof(Decision), nameof(DecisionBrush), nameof(DefectCount), nameof(DetectionTime), nameof(ResultCaption), nameof(InspectionId) })
            OnPropertyChanged(property);
    }

    private static BitmapSource? Decode(byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException) { return null; }
    }

    private void AddEvent(string text)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        if (Events.Count > 100) Events.RemoveAt(Events.Count - 1);
    }
}
