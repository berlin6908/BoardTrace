using System.Collections.ObjectModel;
using System.IO;
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
    public string Upload => Stored.PendingUpload ? "待上传" : Stored.Record.ExecutionStatus == InspectionExecution.Started ? "未生成结果" : "已确认";
}

public sealed class StationViewModel : ObservableObject
{
    private readonly StationOptions options;
    private readonly LocalInspectionStore store;
    private readonly InspectionCoordinator coordinator;
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

    public StationViewModel(StationOptions options)
    {
        this.options = options;
        store = new LocalInspectionStore(options.DatabasePath);
        coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanEdit && !coordinator.IsFaulted && SelectedSample != null && !string.IsNullOrWhiteSpace(ProductId));
        ViewHistoryCommand = new AsyncRelayCommand(ViewHistoryAsync, () => SelectedHistory != null && CanEdit);
        RunCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RunCommand.IsRunning)) return;
            OnPropertyChanged(nameof(CanEdit));
            ViewHistoryCommand.NotifyCanExecuteChanged();
        };
        ViewHistoryCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewHistoryCommand.IsRunning)) return;
            OnPropertyChanged(nameof(CanEdit));
            RunCommand.NotifyCanExecuteChanged();
        };
    }

    public string StationId => options.StationId;
    public string DatabasePath => options.DatabasePath;
    public bool CanEdit => ready && !RunCommand.IsRunning && !ViewHistoryCommand.IsRunning;
    public ObservableCollection<ReplaySample> Samples { get; } = [];
    public ObservableCollection<InspectionRow> History { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
    public ObservableCollection<DefectOverlay> DefectOverlays { get; } = [];
    public AsyncRelayCommand RunCommand { get; }
    public AsyncRelayCommand ViewHistoryCommand { get; }

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
    public string ResultCaption => current is null ? "尚未选择检测档案" : $"{current.ProductId} · 样本 {current.SampleId} · {(current.SourceKind == "ConstructedNormal" ? "构造正常输入" : "数据集回放")}";
    public string InspectionId => current?.Id.ToString() ?? "等待检测";

    public static string DecisionLabel(QualityDecision decision) => decision switch
    {
        QualityDecision.Pass => "合格", QualityDecision.Fail => "缺陷", _ => "未判定"
    };

    public async Task InitializeAsync()
    {
        try
        {
            await Task.Run(store.Initialize);
            var lines = await File.ReadAllLinesAsync(options.ManifestPath);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
                Samples.Add(JsonSerializer.Deserialize<ReplaySample>(line, json) ?? throw new InvalidDataException("回放清单包含空记录。"));
            SelectedSample = Samples.FirstOrDefault();
            await RefreshHistoryAsync();
            ready = true;
            Status = "准备就绪";
            Notice = "选择输入并开始检测。当前为工程回放，不计入生产批次。";
            AddEvent($"工位已启动，载入 {Samples.Count} 个回放输入。");
        }
        catch (Exception error)
        {
            Status = "启动失败";
            Notice = error.Message;
            AddEvent("启动失败：" + error.Message);
        }
        OnPropertyChanged(nameof(CanEdit));
        RunCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync()
    {
        var sample = SelectedSample!;
        var product = ProductId.Trim();
        var source = new ReplayImageSource(options.DataRoot, sample, ConstructedNormal);
        SelectedHistory = null;
        ShowRecord(null);
        Notice = "正在处理，完成本地保存后显示判定。";
        InspectionRecord result;
        try
        {
            result = await coordinator.InspectAsync(StationId, product, source, new Progress<string>(stage => Status = stage));
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
        var rows = await Task.Run(() => store.ReadRecent());
        PendingCount = await Task.Run(store.PendingCount);
        History.Clear();
        foreach (var row in rows) History.Add(new InspectionRow(row));
    }

    private async Task ViewHistoryAsync()
    {
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
