using BoardTrace.Contracts;

namespace BoardTrace.Station.Core;

public sealed record PlcStationStatus(bool Connected, string Message, PlcOutput Output);

// The runner owns the wire handshake. The coordinator remains the only path that
// accepts work, allocates production numbers, detects images and commits results.
public sealed class PlcInspectionRunner(
    LocalInspectionStore store,
    InspectionCoordinator coordinator,
    Func<bool> canAccept,
    Func<PlcInput, Action<InspectionRecord>, CancellationToken, Task<InspectionRecord>> inspect,
    IProgress<PlcStationStatus>? progress = null)
{
    private PlcInspectionState? held;
    private InspectionRecord? historical;
    private Task<InspectionRecord>? inspectionTask;
    private TaskCompletionSource<InspectionRecord>? accepted;
    private InspectionIdentity? activeIdentity;
    private InspectionIdentity? responseIdentity;
    private PlcTriggerDisposition disposition;
    private bool triggerAck;
    private bool triggerHigh;
    private bool localFault;
    private bool connected;
    private bool running;
    private bool ackHigh;
    private bool inputValid;
    private ushort heartbeat;
    private DateTimeOffset nextHeartbeat;
    private string message = "等待连接 PLC";
    private PlcStationStatus? lastReported;

    public async Task RunAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (running) throw new InvalidOperationException("PLC 通信已在运行。");
        running = true;
        PlcConnection? connection = null;
        try
        {
            // Startup recovery must run before enabling any inspection commands.
            held = store.ReadUnacknowledgedPlc();
            while (!cancellationToken.IsCancellationRequested)
            {
                await ObserveInspectionAsync();
                if (DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    heartbeat = unchecked((ushort)(heartbeat + 1));
                    nextHeartbeat = DateTimeOffset.UtcNow.AddMilliseconds(500);
                }

                if (connection is null)
                {
                    try
                    {
                        connection = await PlcConnection.ConnectAsync(host, port, TimeSpan.FromSeconds(2), cancellationToken);
                        connected = true;
                        // An input still high after reconnect must be answered from
                        // durable identity, including a TriggerAck whose reply was lost.
                        triggerHigh = false;
                        message = "PLC 已连接";
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch (Exception error)
                    {
                        connected = false;
                        message = $"PLC 连接中断：{error.Message}";
                        Report();
                        await Task.Delay(500, cancellationToken);
                        continue;
                    }
                }

                (PlcInput Input, string? Error) received;
                try { received = await connection.ReadInputAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    connection.Dispose();
                    connection = null;
                    connected = false;
                    message = $"PLC 读取中断：{error.Message}";
                    Report();
                    continue;
                }

                ProcessInput(received.Input, received.Error);
                await ObserveInspectionAsync();
                try { await connection.WriteOutputAsync(Output(), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    connection.Dispose();
                    connection = null;
                    connected = false;
                    message = $"PLC 结果发送中断：{error.Message}";
                }
                Report();
                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            // Stopping communication does not abandon an already dispatched image.
            // Its result remains durable for the next connection, even without ACK.
            if (inspectionTask is not null)
            {
                try { await inspectionTask; } catch { /* ObserveInspectionAsync records the fault below. */ }
                await ObserveInspectionAsync();
            }
            connected = false;
            message = held is null ? "PLC 通信已停止" : "PLC 通信已停止；未确认结果保留";
            if (connection is not null)
            {
                try { await connection.WriteOutputAsync(Output(), CancellationToken.None); }
                catch { /* Reconnect restores the durable result; stale heartbeat is not Ready. */ }
                connection.Dispose();
            }
            running = false;
            Report();
        }
    }

    private void ProcessInput(PlcInput input, string? error)
    {
        ackHigh = input.ResultsAck;
        inputValid = error is null;
        try
        {
            if (error is null && input.ResultsAck && held is { AckAt: null } pending &&
                IsFinished(pending.Record) && Identity(pending.Record) == input.Identity)
            {
                coordinator.ConfirmPlcAck(pending.Record.Id);
                historical = pending.Record;
                held = null;
                message = "PLC 已确认，等待握手信号复位";
            }

            if (!input.Trigger)
            {
                triggerHigh = false;
                triggerAck = false;
                return;
            }
            if (triggerHigh) return;
            triggerHigh = true;
            responseIdentity = input.Identity;
            if (error is not null)
            {
                Reject(PlcTriggerDisposition.InvalidRejected, error);
                return;
            }
            if (held is not null && Identity(held.Record) != input.Identity)
            {
                Reject(PlcTriggerDisposition.BusyRejected, "前一件仍在检测或等待 PLC 确认");
                return;
            }

            var existing = store.ReadPlcTrigger(input.Identity);
            if (existing is not null)
            {
                if (existing.Record.ProductId != input.ProductId || existing.Record.SampleId != input.SampleId)
                {
                    Reject(PlcTriggerDisposition.InvalidRejected, "相同触发身份的产品或样本发生变化");
                    return;
                }
                triggerAck = true;
                disposition = PlcTriggerDisposition.Duplicate;
                if (existing.AckAt is null) held = existing;
                else historical = existing.Record;
                message = "重复触发，返回原检测状态";
                return;
            }
            if (inspectionTask is not null || coordinator.IsBusy || held is not null)
            {
                Reject(PlcTriggerDisposition.BusyRejected, "工位忙，未接受新触发");
                return;
            }
            if (localFault || coordinator.IsFaulted || input.ResultsAck || !canAccept())
            {
                Reject(PlcTriggerDisposition.InvalidRejected, "工位未就绪，请检查批次、人员授权和握手状态");
                return;
            }

            historical = null;
            triggerAck = false;
            disposition = PlcTriggerDisposition.None;
            accepted = new TaskCompletionSource<InspectionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            activeIdentity = input.Identity;
            var notification = accepted;
            // The caller performs the existing personnel/batch checks before the
            // coordinator accepts. A network reconnect never cancels this task.
            inspectionTask = inspect(input, record => notification.TrySetResult(record), CancellationToken.None);
            message = "正在验证接件条件";
        }
        catch (InspectionRejectedException errorRejected)
        {
            Reject(PlcTriggerDisposition.InvalidRejected, errorRejected.Message);
        }
        catch (Exception errorLocal)
        {
            localFault = true;
            Reject(PlcTriggerDisposition.InvalidRejected, $"工位本地故障：{errorLocal.Message}");
        }
    }

    private async Task ObserveInspectionAsync()
    {
        if (accepted?.Task.IsCompletedSuccessfully == true)
        {
            held = new PlcInspectionState(accepted.Task.Result, null);
            if (responseIdentity == activeIdentity && triggerHigh)
            {
                triggerAck = true;
                disposition = PlcTriggerDisposition.Accepted;
            }
            message = "触发已持久接受，正在检测";
            accepted = null;
        }
        if (inspectionTask?.IsCompleted != true) return;
        try
        {
            var completed = await inspectionTask;
            // The coordinator returns only after the image/result/outbox commit.
            // A duplicate may have exposed that commit while the caller was still
            // refreshing its UI. Preserve an ACK received before the task returned.
            var persisted = store.ReadPlcTrigger(Identity(completed))
                ?? throw new InvalidOperationException("已完成检测缺少本地 PLC 触发记录。");
            held = persisted.AckAt is null ? persisted : null;
            if (persisted.AckAt is not null) historical = persisted.Record;
            message = held is null ? "PLC 已确认，等待握手信号复位" : "检测结果已保存，等待 PLC 确认";
        }
        catch (InspectionRejectedException error) { Reject(PlcTriggerDisposition.InvalidRejected, error.Message); }
        catch (Exception error)
        {
            localFault = coordinator.IsFaulted || held is not null;
            Reject(PlcTriggerDisposition.InvalidRejected, $"检测未完成：{error.Message}");
        }
        finally { inspectionTask = null; accepted = null; activeIdentity = null; }
    }

    private void Reject(PlcTriggerDisposition rejection, string reason)
    {
        triggerAck = true;
        disposition = rejection;
        message = reason;
    }

    private PlcOutput Output()
    {
        var fault = localFault || coordinator.IsFaulted;
        var result = held?.Record ?? historical;
        var valid = held is { AckAt: null } && result is not null && IsFinished(result);
        var busy = inspectionTask is not null || coordinator.IsBusy || held is { AckAt: null };
        return new PlcOutput(connected && inputValid && !fault && !busy && held is null && !triggerHigh && !ackHigh && canAccept(),
            triggerAck, busy, valid, fault, disposition, responseIdentity,
            result is null ? null : Identity(result),
            result is null || !IsFinished(result) ? PlcResultCode.None : result.Decision switch
            {
                QualityDecision.Pass => PlcResultCode.Pass,
                QualityDecision.Fail => PlcResultCode.Fail,
                _ => PlcResultCode.NotEvaluated
            }, result?.Id, checked((uint)(result?.ProductionSequence ?? 0)), heartbeat);
    }

    private void Report()
    {
        var status = new PlcStationStatus(connected, message, Output());
        if (lastReported is not null && lastReported == status with { Output = status.Output with { Heartbeat = lastReported.Output.Heartbeat } }) return;
        lastReported = status;
        progress?.Report(status);
    }

    private static bool IsFinished(InspectionRecord record) => record.ExecutionStatus != InspectionExecution.Started;
    private static InspectionIdentity Identity(InspectionRecord record) =>
        new(record.ControllerSessionId!.Value, record.TriggerSequence!.Value);
}
