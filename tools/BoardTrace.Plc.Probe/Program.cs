using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using static ProbeFixture;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);
    public static async Task Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "station-child")
        {
            await RecoveryProbe.RunStationAsync(args[1], Path.GetFullPath(args[2]), int.Parse(args[3]));
            return;
        }
        if (args.Length == 2 && args[0] is "kill-started" or "kill-completed" or "kill-ack")
        {
            await RecoveryProbe.RunAsync(args[0], Path.GetFullPath(args[1]));
            return;
        }
        if (args.Length != 2 || !new[] { "normal", "duplicate-trigger", "busy", "lost-ack", "reconnect", "busy-during-capture", "ack-crash", "completion-gate" }.Contains(args[0]))
            throw new ArgumentException("Usage from repository root: <normal|duplicate-trigger|busy|lost-ack|reconnect|busy-during-capture|ack-crash|completion-gate> <new-output-directory>");
        var root = Directory.GetCurrentDirectory();
        var scenario = args[0];
        var folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
        var (sample, reference, tested, store, version, batch, coordinator) = await ProbeFixture.CreateAsync(root, folder, scenario);
        var versionId = version.Bundle.VersionId;
        using var log = new StreamWriter(Path.Combine(folder, "runner.jsonl")) { AutoFlush = true };
        var snapshots = new ConcurrentQueue<PlcStationStatus>();
        var progress = new ImmediateProgress(status => { snapshots.Enqueue(status); log.WriteLine(JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, status }, LineJson)); });
        var captures = 0; var accepted = 0;
        var runner = new PlcInspectionRunner(store, coordinator,
            () => store.ReadActiveBatch() is { Status: BatchStatus.InProgress, Session: { } session } active &&
                session.OperatorId == Actor.Id && session.ExpiresAt > DateTimeOffset.UtcNow && active.AcceptedProductionCount < batch.PlannedQuantity,
            async (trigger, onStarted, token) =>
            {
                Require(trigger.SampleId == sample.SampleId, "Trigger selects an allowed input sample.");
                var source = new CountedSource(new PublishedReplayImageSource(Path.Combine(root, "data"), sample),
                    () => Interlocked.Increment(ref captures), scenario == "busy-during-capture" ? 3000 : 0);
                var record = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, trigger.ProductId, Actor, source,
                    cancellationToken: token, identity: trigger.Identity, startedCommitted: record => { Interlocked.Increment(ref accepted); onStarted(record); });
                if (scenario == "completion-gate")
                {
                    await File.WriteAllTextAsync(Path.Combine(folder, "delegate-committed"), record.Id.ToString("D"), token);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (!File.Exists(Path.Combine(folder, "release-delegate"))) await Task.Delay(20, timeout.Token);
                    Require(store.ReadPlcTrigger(trigger.Identity)!.AckAt is not null, "ACK persisted before the inspection delegate returned.");
                    await Write(folder, "delegate-return.json", new { atUtc = DateTimeOffset.UtcNow, record.Id, plc = store.ReadPlcTrigger(trigger.Identity) });
                }
                return record;
            }, progress);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var stop = new CancellationTokenSource();
        var communication = runner.RunAsync("127.0.0.1", port, stop.Token);
        var executions = new List<object>();
        try
        {
            if (scenario == "reconnect")
            {
                executions.Add(await Python(root, folder, port, "lost-ack", "initial", 2, 5, expectedExit: 1, expectedError: "Withheld ACK"));
                var held = store.ReadUnacknowledgedPlc();
                Require(held?.Record.ExecutionStatus == InspectionExecution.Completed && captures == 1 && accepted == 1, "Committed result retained before reconnect.");
                await Write(folder, "before-reconnect.json", new { held, captures, accepted, nextProductionSequence = store.ReadActiveBatch()!.NextProductionSequence });
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (!snapshots.Any(status => !status.Connected && status.Output.ResultsValid)) await Task.Delay(50, wait.Token);
                executions.Add(await Python(root, folder, port, "lost-ack", "resumed", 20, 3));
            }
            else if (scenario == "ack-crash")
            {
                executions.Add(await Python(root, folder, port, scenario, "initial", 20, 1, expectedExit: 73));
                Require(File.Exists(Path.Combine(folder, "before-pending-finish")), "Python exited at the durable pending finish boundary.");
                var acknowledged = store.ReadRecent().Single(row => row.Record.Purpose == InspectionPurpose.Production).Record;
                var acknowledgedIdentity = new InspectionIdentity(acknowledged.ControllerSessionId!.Value, acknowledged.TriggerSequence!.Value);
                Require(store.ReadPlcTrigger(acknowledgedIdentity)!.AckAt is not null && ReadControllerCount(folder, "Pending") == 1 &&
                    ReadControllerCount(folder, "Results") == 1, "Station ACK and PLC ledger committed while PLC Pending survived process exit.");
                coordinator.EndOperatorSession();
                Require(store.ReadActiveBatch()!.Session is null, "Operator session cleared before historical recovery.");
                await Write(folder, "before-history-recovery.json", new { plc = store.ReadPlcTrigger(acknowledgedIdentity), active = store.ReadActiveBatch(), captures, accepted });
                executions.Add(await Python(root, folder, port, "normal", "resumed", 20, 1));
                Require(snapshots.Any(value => value.Connected && !value.Output.TriggerReady && !value.Output.ResultsValid &&
                    value.Output.TriggerDisposition == PlcTriggerDisposition.Duplicate), "History recovered with no session and no new-product Ready.");
            }
            else executions.Add(await Python(root, folder, port, scenario, "single", 20, 1));
        }
        finally
        {
            stop.Cancel();
            await communication.WaitAsync(TimeSpan.FromSeconds(8));
        }
        Require(!coordinator.IsFaulted && captures == 1 && accepted == 1, "One real capture and accepted Started per unique trigger.");
        var local = store.ReadRecent(10).Select(row => store.Get(row.Record.Id)!).ToArray();
        Require(local.Length == 2 && store.PendingCount() == 2, "One first article and one production result in local outbox.");
        var production = local.Single(record => record.Purpose == InspectionPurpose.Production);
        Require(production.ExecutionStatus == InspectionExecution.Completed && production.Decision == QualityDecision.Fail && production.Defects.Count > 0,
            "Validation image actually produced detected defects.");
        Require(production.BatchId == batch.Id && production.RecipeId == versionId.ToString("D") && production.ProductionSequence == 1 &&
            production.SourceKind == "Replay" && production.OperatorId == Actor.Id, "Production provenance and sequence.");
        Require(production.TestedImage!.SequenceEqual(tested) && production.ReferenceImage!.SequenceEqual(reference), "SQLite images match actual input bytes.");
        var identity = new InspectionIdentity(production.ControllerSessionId!.Value, production.TriggerSequence!.Value);
        Require(store.ReadPlcTrigger(identity)!.AckAt is not null && store.ReadUnacknowledgedPlc() is null, "PLC ACK persisted; original archive retained.");
        Require(store.ReadActiveBatch()!.NextProductionSequence == 2, "Duplicate and busy probe consume no production sequence.");
        using var plcDb = new SqliteConnection($"Data Source={Path.Combine(folder, "controller.db")}"); plcDb.Open();
        using var read = plcDb.CreateCommand(); read.CommandText = "SELECT ResultJson FROM Results;";
        var ledger = new List<JsonElement>();
        using (var reader = read.ExecuteReader()) while (reader.Read()) ledger.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        Require(ledger.Count == 1, "PLC counted exactly one unique result.");
        var wire = ledger.Single();
        Require(wire.GetProperty("inspectionId").GetGuid() == production.Id && wire.GetProperty("sessionId").GetGuid() == identity.ControllerSessionId &&
            wire.GetProperty("sequence").GetUInt32() == identity.TriggerSequence && wire.GetProperty("productionSequence").GetInt32() == 1 &&
            wire.GetProperty("resultCode").GetInt32() == (int)PlcResultCode.Fail, "Actual wire result matches SQLite identity, code and quantity.");
        read.CommandText = "SELECT COUNT(*) FROM Pending;"; Require((long)read.ExecuteScalar()! == 0, "PLC handshake completed its durable pending identity.");
        if (scenario == "duplicate-trigger") Require(snapshots.Any(value => value.Output.TriggerDisposition == PlcTriggerDisposition.Duplicate), "Actual Duplicate disposition observed.");
        if (scenario == "busy") Require(snapshots.Any(value => value.Output.TriggerDisposition == PlcTriggerDisposition.BusyRejected), "Actual BusyRejected disposition observed.");
        if (scenario == "busy-during-capture") Require(snapshots.Any(value => value.Output.TriggerDisposition == PlcTriggerDisposition.BusyRejected &&
            value.Output.Busy && !value.Output.ResultsValid), "Busy rejection before a quality result exists.");
        Require(snapshots.Any(value => value.Output.TriggerDisposition == PlcTriggerDisposition.Accepted), "Accepted followed Started commit.");
        Require(snapshots.Any(value => value.Output.ResultsValid && value.Output.ResultIdentity == identity), "Committed ResultsValid observed.");
        foreach (var record in local) await Write(folder, $"inspection-{record.Id}.json", record);
        var report = new { scope = Scope, scenario, port, executions, batchId = batch.Id, recipeVersionId = versionId,
            captures, accepted, stationRecords = local.Length, stationImages = local.Sum(record => (record.TestedImage is null ? 0 : 1) + (record.ReferenceImage is null ? 0 : 1)),
            outbox = store.PendingCount(), controllerResults = ledger.Count, plcPending = 0, plcAckAt = store.ReadPlcTrigger(identity)!.AckAt,
            production.Id, production.ProductId, production.ControllerSessionId, production.TriggerSequence, production.ProductionSequence,
            production.Decision, defectCount = production.Defects.Count, production.DetectionMs, contentHash = InspectionTransfer.Hash(production),
            testedSha256 = Hash(tested), referenceSha256 = Hash(reference), allChecksPassed = true };
        await Write(folder, "comparison.json", report); Console.WriteLine(JsonSerializer.Serialize(report, Json));
    }

    private static async Task<object> Python(string root, string folder, int port, string scenario, string attempt, int timeout, int ackDelay,
        int expectedExit = 0, string? expectedError = null)
    {
        var start = new ProcessStartInfo(Path.Combine(root, ".venv/Scripts/python.exe"))
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var fault = scenario is "busy-during-capture" or "ack-crash" or "completion-gate";
        if (fault)
        {
            start.ArgumentList.Add(Path.Combine(root, "tools/BoardTrace.Plc.Probe/fault_scenarios.py"));
            start.Environment["BOARDTRACE_PLC_PROBE_FAULT"] = scenario;
            start.Environment["BOARDTRACE_PLC_PROBE_FOLDER"] = folder;
        }
        else { start.ArgumentList.Add("-m"); start.ArgumentList.Add("tools.simulator"); }
        foreach (var arg in new[] { "--scenario", fault ? "normal" : scenario, "--host", "127.0.0.1", "--port", port.ToString(), "--count", "1",
            "--product-id", "SIM-PLC-PRODUCT", "--samples", Path.Combine(folder, "samples.jsonl"), "--state", Path.Combine(folder, "controller.db"),
            "--output", Path.Combine(folder, $"simulator-{attempt}.jsonl"), "--timeout", timeout.ToString(), "--ack-delay", ackDelay.ToString() }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(50)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        await File.WriteAllTextAsync(Path.Combine(folder, $"python-{attempt}.stdout.txt"), await stdout);
        await File.WriteAllTextAsync(Path.Combine(folder, $"python-{attempt}.stderr.txt"), await stderr);
        Require(process.ExitCode == expectedExit && (expectedError is null || (await stderr).Contains(expectedError, StringComparison.Ordinal)),
            $"Python {scenario}/{attempt} exit {process.ExitCode}; inspect local stderr/events.");
        return new { scenario, attempt, pid = process.Id, process.ExitCode, expectedExit };
    }
    private static long ReadControllerCount(string folder, string table)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(folder, "controller.db")}"); db.Open();
        using var read = db.CreateCommand(); read.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)read.ExecuteScalar()!;
    }
    private static Task Write<T>(string folder, string name, T value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, Json));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ImmediateProgress(Action<PlcStationStatus> report) : IProgress<PlcStationStatus> { public void Report(PlcStationStatus value) => report(value); }
    private sealed class CountedSource(IImageSource source, Action captured, int delayMs) : IImageSource
    {
        public string SampleId => source.SampleId; public string SourceKind => source.SourceKind;
        public async Task<CapturedPair> CaptureAsync(CancellationToken token)
        {
            captured();
            if (delayMs > 0) await Task.Delay(delayMs, token);
            return await source.CaptureAsync(token);
        }
    }
}
