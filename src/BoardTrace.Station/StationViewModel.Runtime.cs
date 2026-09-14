using System.Net.Http;
using BoardTrace.Station.Core;

namespace BoardTrace.Station;

public sealed partial class StationViewModel
{
    private StationRuntimeClient? runtimeClient;
    private DateTimeOffset nextRuntimeReport;
    private string runtimeStatus = "尚未上报";
    private string runtimeNotice = "设备将每 5 秒上报本地接件、积压和 PLC 确认状态。";
    private DateTimeOffset? runtimeReceivedAt;

    public string RuntimeStatus { get => runtimeStatus; private set => SetProperty(ref runtimeStatus, value); }
    public string RuntimeNotice { get => runtimeNotice; private set => SetProperty(ref runtimeNotice, value); }
    public DateTimeOffset? RuntimeReceivedAt { get => runtimeReceivedAt; private set => SetProperty(ref runtimeReceivedAt, value); }

    // Runs serially with device uploads, including when no personnel session is present.
    private async Task ReportRuntimeAsync(CancellationToken token)
    {
        if (runtimeClient is null || DateTimeOffset.UtcNow < nextRuntimeReport) return;
        nextRuntimeReport = DateTimeOffset.UtcNow.AddSeconds(5);
        try
        {
            var state = coordinator.IsFaulted ? "本地存储故障 · 停止接件"
                : activeBatch?.Status == BoardTrace.Contracts.BatchStatus.Closed ? "中央已关闭批次"
                : currentOperator is null ? "本地档案与设备恢复" : Status;
            var alarm = coordinator.IsFaulted || !ready ? Notice : null;
            if (alarm?.Length > 2000) alarm = alarm[..2000];
            var report = await Task.Run(() => store.ReadRuntime(state, alarm), token);
            var receipt = await runtimeClient.ReportAsync(report, token);
            RuntimeReceivedAt = receipt.ReceivedAt;
            RuntimeStatus = "最近上报成功";
            RuntimeNotice = $"中央接收于 {receipt.ReceivedAt.LocalDateTime:HH:mm:ss} · 待上传 {report.PendingUploads}"
                + $" · {(report.HasStartedInspection ? "检测进行中" : "无未完成检测")}"
                + $" · {(report.HasUnacknowledgedPlc ? "等待 PLC 确认" : "无待确认 PLC 结果")}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            RuntimeStatus = error is HttpRequestException or OperationCanceledException ? "上报连接失败" : "上报需处理";
            var nextNotice = $"{error.Message}"
                + (RuntimeReceivedAt is { } last ? $" 最近一次中央接收：{last.LocalDateTime:MM-dd HH:mm:ss}。" : " 尚未收到中央上报回执。");
            if (RuntimeNotice != nextNotice) AddEvent("工位上报失败：" + nextNotice);
            RuntimeNotice = nextNotice;
        }
    }
}
