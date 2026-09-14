using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using CommunityToolkit.Mvvm.Input;

namespace BoardTrace.Station;

public sealed partial class StationViewModel
{
    private string plcHost = "127.0.0.1";
    private string plcPort = "1502";
    private string plcStatus = "PLC 通信未启动";
    private string plcSignals = "等待连接";
    private bool plcRunning;
    private bool plcInspecting;
    private bool plcWaitingAck;
    private CancellationTokenSource? plcCancellation;
    private Task? plcRunTask;
    private Task<InspectionRecord>? plcInspectionTask;

    public string PlcHost { get => plcHost; set { if (SetProperty(ref plcHost, value)) StartPlcCommand.NotifyCanExecuteChanged(); } }
    public string PlcPort { get => plcPort; set { if (SetProperty(ref plcPort, value)) StartPlcCommand.NotifyCanExecuteChanged(); } }
    public string PlcStatus { get => plcStatus; private set => SetProperty(ref plcStatus, value); }
    public string PlcSignals { get => plcSignals; private set => SetProperty(ref plcSignals, value); }
    public bool CanEditPlcEndpoint => !plcRunning && !stopping;
    public RelayCommand StartPlcCommand { get; private set; } = null!;
    public AsyncRelayCommand StopPlcCommand { get; private set; } = null!;

    private void InitializePlcCommands()
    {
        StartPlcCommand = new RelayCommand(StartPlc, () => !plcRunning && archiveReady && !stopping && !signingOut
            && !RunCommand.IsRunning && !IsRecipeBusy && !IsBatchBusy && !string.IsNullOrWhiteSpace(PlcHost)
            && int.TryParse(PlcPort, out var port) && port is >= 1 and <= 65535);
        StopPlcCommand = new AsyncRelayCommand(StopPlcAsync, () => plcRunning);
    }

    private bool CanAcceptPlc() => CanEdit && !coordinator.IsFaulted && CanContinueProduction();

    private void StartPlc()
    {
        plcCancellation = new CancellationTokenSource();
        plcRunning = true;
        NotifyPlcAccess();
        var runner = new PlcInspectionRunner(store, coordinator, CanAcceptPlc, InspectPlcAsync,
            new Progress<PlcStationStatus>(UpdatePlcStatus));
        plcRunTask = RunPlcAsync(runner, PlcHost.Trim(), int.Parse(PlcPort), plcCancellation);
    }

    private async Task RunPlcAsync(PlcInspectionRunner runner, string host, int port, CancellationTokenSource cancellation)
    {
        try { await runner.RunAsync(host, port, cancellation.Token); }
        catch (Exception error)
        {
            PlcStatus = "PLC 通信停止：" + error.Message;
            AddEvent(PlcStatus);
        }
        finally
        {
            plcRunning = false;
            cancellation.Dispose();
            if (plcCancellation == cancellation) plcCancellation = null;
            NotifyPlcAccess();
        }
    }

    private async Task StopPlcAsync()
    {
        var task = plcRunTask;
        if (plcCancellation is { } cancellation) await cancellation.CancelAsync();
        if (task is not null) await task;
    }

    private void UpdatePlcStatus(PlcStationStatus state)
    {
        var output = state.Output;
        if (PlcStatus != state.Message) AddEvent(state.Message);
        PlcStatus = state.Message;
        plcWaitingAck = output.ResultsValid;
        var trigger = output.TriggerIdentity is { } id ? $"{id.ControllerSessionId.ToString("N")[..8]} / {id.TriggerSequence}" : "—";
        PlcSignals = $"就绪 {(output.TriggerReady ? "是" : "否")} · 忙 {(output.Busy ? "是" : "否")} · 触发 {trigger}"
            + (output.ResultsValid ? $" · 等待确认 {output.InspectionId}" : "")
            + (output.Fault ? " · 工位故障" : "");
        NotifyPlcAccess();
    }

    private void NotifyPlcAccess()
    {
        OnPropertyChanged(nameof(CanEditPlcEndpoint));
        StartPlcCommand.NotifyCanExecuteChanged();
        StopPlcCommand.NotifyCanExecuteChanged();
        NotifyAccessChanged();
    }

    private Task<InspectionRecord> InspectPlcAsync(PlcInput input, Action<InspectionRecord> accepted, CancellationToken token) =>
        plcInspectionTask = InspectPlcCoreAsync(input, accepted, token);

    private async Task<InspectionRecord> InspectPlcCoreAsync(PlcInput input, Action<InspectionRecord> accepted, CancellationToken token)
    {
        if (!CanAcceptPlc()) throw new InspectionRejectedException("PLC 只执行已启动、首件批准的当前生产批次。");
        var sample = Samples.SingleOrDefault(item => item.SampleId == input.SampleId)
            ?? throw new InspectionRejectedException("PLC 样本不属于当前批次固定方案。");
        plcInspecting = true;
        NotifyAccessChanged();
        try
        {
            var actor = await AuthorizeInspectionAsync(InspectionPurpose.Production);
            if (actor is null || stopping || signingOut)
                throw new InspectionRejectedException("人员授权未通过，PLC 触发未接件。");
            SelectedSample = sample;
            ProductId = input.ProductId;
            SelectedHistory = null;
            ShowRecord(null);
            // PLC selects an input asset. The UI's constructed-normal switch does
            // not alter a device-triggered production image.
            var result = await coordinator.InspectAsync(InspectionPurpose.Production, StationId, input.ProductId, actor,
                CreateReplaySource(sample, false), new Progress<string>(stage => Status = stage), token, input.Identity, accepted);
            ShowRecord(result);
            Status = result.ExecutionStatus == InspectionExecution.Completed ? "检测完成 · 已保存" : "执行失败 · 已保存";
            Notice = result.Error ?? "本地档案已提交，等待 PLC 确认并同步中央。";
            AddEvent($"PLC {input.Identity.TriggerSequence} · {input.ProductId} · {Decision} · 本地提交成功。");
            try { await RefreshHistoryAsync(); }
            catch (Exception error) { Notice += " 档案列表刷新失败：" + error.Message; }
            return result;
        }
        finally { plcInspecting = false; NotifyAccessChanged(); }
    }

    private async Task AwaitPlcInspectionAsync()
    {
        if (plcInspectionTask is not { } task) return;
        try { await task; }
        catch { /* The runner reports rejection or fault and preserves any accepted identity. */ }
    }
}
