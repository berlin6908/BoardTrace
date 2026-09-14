using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using CommunityToolkit.Mvvm.Input;

namespace BoardTrace.Station;

public sealed record ReworkOrderChoice(ReworkOrder Order)
{
    public string Label => $"{Order.ProductId} · {Order.CreatedAt.LocalDateTime:MM-dd HH:mm}";
}

public sealed partial class StationViewModel
{
    private CachedReworkOrder? activeRework;
    private ReworkOrderChoice? selectedReworkOrder;
    private string reinspectionNotice = "在线获取质量人员下发的返工指令，再选择进入复检模式。";

    public ObservableCollection<ReworkOrderChoice> ReworkOrders { get; } = [];
    public AsyncRelayCommand RefreshReworkOrdersCommand { get; private set; } = null!;
    public AsyncRelayCommand EnterReinspectionCommand { get; private set; } = null!;
    public AsyncRelayCommand ExitReinspectionCommand { get; private set; } = null!;
    public ReworkOrderChoice? SelectedReworkOrder
    {
        get => selectedReworkOrder;
        set { if (SetProperty(ref selectedReworkOrder, value)) NotifyReworkCommands(); }
    }
    public bool IsReinspectionMode => activeRework is not null;
    public bool CanEditInspectionInput => CanEdit && !IsReinspectionMode;
    public string ReinspectionNotice { get => reinspectionNotice; private set => SetProperty(ref reinspectionNotice, value); }
    public string ActiveReworkDescription => activeRework is null ? "当前未进入复检模式"
        : $"返工指令 {activeRework.Order.Id}\n原检测 {activeRework.Order.OriginalInspectionId}\n{activeRework.Order.Reason}"
            + (activeRework.InspectionId is { } id ? $"\n本次已接件：{id}。等待结果上传及质量复核；同一指令不能重采。" : "\n待执行 · 固定原产品与样本");
    private bool IsReworkBusy => RefreshReworkOrdersCommand.IsRunning || EnterReinspectionCommand.IsRunning || ExitReinspectionCommand.IsRunning;

    private bool CanContinueReinspection() => HasCurrentBatchSession() && activeRework is { InspectionId: null }
        && activeRework.ArchiveId == activeBatch!.ArchiveId && activeRework.Order.BatchId == activeBatch.Batch.Id;

    private void InitializeReworkCommands()
    {
        RefreshReworkOrdersCommand = new AsyncRelayCommand(RefreshReworkOrdersAsync, () => CanEdit && activeBatch is not null
            && activeBatch.Status != BatchStatus.Closed && !coordinator.IsFaulted);
        EnterReinspectionCommand = new AsyncRelayCommand(EnterReinspectionAsync, () => CanEdit && !coordinator.IsFaulted
            && SelectedReworkOrder is not null && activeBatch?.Status is BatchStatus.Approved or BatchStatus.InProgress);
        ExitReinspectionCommand = new AsyncRelayCommand(ExitReinspectionAsync, () => CanEdit && IsReinspectionMode && !coordinator.IsFaulted);
        foreach (var command in new[] { RefreshReworkOrdersCommand, EnterReinspectionCommand, ExitReinspectionCommand })
            command.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(AsyncRelayCommand.IsRunning)) NotifyAccessChanged();
            };
    }

    private void NotifyReworkCommands()
    {
        RefreshReworkOrdersCommand.NotifyCanExecuteChanged();
        EnterReinspectionCommand.NotifyCanExecuteChanged();
        ExitReinspectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditInspectionInput));
    }

    private void UpdateReworkDisplay(CachedReworkOrder? selected, IReadOnlyList<ReworkOrder> pending)
    {
        activeRework = selected;
        var selectedId = SelectedReworkOrder?.Order.Id ?? selected?.Order.Id;
        ReworkOrders.Clear();
        foreach (var order in pending) ReworkOrders.Add(new ReworkOrderChoice(order));
        SelectedReworkOrder = ReworkOrders.FirstOrDefault(choice => choice.Order.Id == selectedId) ?? ReworkOrders.FirstOrDefault();
        if (activeRework is not null)
        {
            ProductId = activeRework.Order.ProductId;
            SelectedSample = Samples.FirstOrDefault(sample => sample.SampleId == activeRework.Order.SampleId);
        }
        OnPropertyChanged(nameof(IsReinspectionMode));
        OnPropertyChanged(nameof(ActiveReworkDescription));
        OnPropertyChanged(nameof(CanEditInspectionInput));
    }

    private async Task RefreshReworkOrdersAsync()
    {
        var batchId = activeBatch!.Batch.Id;
        ReinspectionNotice = "正在在线读取返工指令…";
        try
        {
            var orders = await operatorClient!.GetFromJsonAsync<ReworkOrder[]>($"api/batches/{batchId}/rework-orders", uploadCancellation.Token)
                ?? throw new InvalidDataException("中央未返回返工指令列表。");
            await Task.Run(() => store.CacheReworkOrders(batchId, orders));
            await RefreshBatchStateAsync();
            ReinspectionNotice = ReworkOrders.Count == 0 ? "当前批次没有待执行返工指令。" : $"已缓存 {ReworkOrders.Count} 条待执行指令；选择后明确进入复检模式。";
        }
        catch (Exception error)
        {
            BatchOperationFailed("获取返工指令失败", error, personnel: true);
            ReinspectionNotice = BatchNotice;
        }
    }

    private async Task EnterReinspectionAsync()
    {
        var order = SelectedReworkOrder!.Order;
        try
        {
            if (!Samples.Any(sample => sample.SampleId == order.SampleId))
                throw new InspectionRejectedException("返工指令样本不在当前固定方案的回放输入中。");
            coordinator.UseReworkOrder(order.Id);
            await RefreshBatchStateAsync();
            ReinspectionNotice = "已进入复检模式。产品与样本固定；完成后不会自动接普通生产件。";
            AddEvent($"进入复检 · {order.ProductId} · 原检测 {order.OriginalInspectionId}。");
        }
        catch (Exception error) { ReinspectionNotice = "未切换复检指令：" + error.Message; }
    }

    private async Task ExitReinspectionAsync()
    {
        try
        {
            coordinator.ExitReinspection();
            await RefreshBatchStateAsync();
            ProductId = $"SIM-{DateTime.Now:yyyyMMdd-HHmmssfff}";
            ReinspectionNotice = "已退出复检模式；普通生产仍受原计划数量限制。";
            AddEvent("已明确退出复检模式。");
        }
        catch (Exception error) { ReinspectionNotice = "未退出复检模式：" + error.Message; }
    }
}
