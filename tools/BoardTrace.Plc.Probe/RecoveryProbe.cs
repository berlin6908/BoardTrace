using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;

internal static class RecoveryProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string scenario, string folder)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var root = Directory.GetCurrentDirectory();
        var fixture = await ProbeFixture.CreateAsync(root, folder, scenario);
        var store = fixture.Store;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var initial = new OwnedProcess(StationStart(root, folder, port, scenario), folder, "station-initial");
        await using var python = new OwnedProcess(PythonStart(root, folder, port, scenario), folder, "python");
        var boundary = scenario switch
        {
            "kill-started" => "initial-trigger-accepted",
            "kill-completed" => "python-before-ack",
            _ => "python-after-ack"
        };
        await WaitForFileAsync(folder, boundary);
        if (scenario == "kill-started") await WaitForFileAsync(folder, "capture-blocked");
        var original = store.Get(store.ReadRecent().Single(row => row.Record.Purpose == InspectionPurpose.Production).Record.Id)!;
        var identity = new InspectionIdentity(original.ControllerSessionId!.Value, original.TriggerSequence!.Value);
        var plcBefore = store.ReadPlcTrigger(identity)!;
        var pendingBefore = ControllerCount(folder, "Pending");
        var ledgerBefore = ControllerCount(folder, "Results");
        Require(pendingBefore == 1, "PLC has the original durable pending identity at the kill boundary.");
        Require(original.ExecutionStatus == (scenario == "kill-started" ? InspectionExecution.Started : InspectionExecution.Completed), "Actual database commit reached the requested kill boundary.");
        Require((plcBefore.AckAt is not null) == (scenario == "kill-ack"), "Actual ACK persistence matches the requested boundary.");
        Require(ledgerBefore == (scenario == "kill-started" ? 0 : 1), "PLC counted only a completed wire result before termination.");
        if (scenario == "kill-started")
            Require(original.TestedImage is null && original.ReferenceImage is null && store.PendingCount() == 1, "Started has no prematurely committed image/result/outbox.");
        await WriteAsync(folder, "before-kill.json", new { atUtc = DateTimeOffset.UtcNow, original, plcBefore, pendingBefore, ledgerBefore,
            contentHash = InspectionTransfer.Hash(original), active = store.ReadActiveBatch() });
        var killed = await initial.KillAsync("Requested OS termination at " + scenario);
        Require(killed.ExitCode != 0, "The initial station was forcibly terminated, not gracefully reconstructed.");
        Require(InspectionTransfer.Hash(store.Get(original.Id)!) == InspectionTransfer.Hash(original), "No late result commit occurred after the observed kill boundary.");

        await using var restored = new OwnedProcess(StationStart(root, folder, port, "restored"), folder, "station-restored");
        await WaitForFileAsync(folder, "restored-ready.json");
        File.WriteAllText(Path.Combine(folder, "continue-handshake"), DateTimeOffset.UtcNow.ToString("O"));
        var pythonExit = await python.WaitSuccessAsync();
        File.WriteAllText(Path.Combine(folder, "restored-stop"), "handshake complete");
        var restoredExit = await restored.WaitSuccessAsync();

        var final = store.Get(original.Id)!;
        var finalPlc = store.ReadPlcTrigger(identity)!;
        var interrupted = scenario == "kill-started";
        Require(final.ExecutionStatus == (interrupted ? InspectionExecution.Interrupted : InspectionExecution.Completed) &&
            final.Decision == (interrupted ? QualityDecision.NotEvaluated : QualityDecision.Fail), "Startup recovery reports the actual terminal outcome.");
        Require(final.Id == original.Id && final.ControllerSessionId == original.ControllerSessionId && final.TriggerSequence == original.TriggerSequence &&
            final.Purpose == InspectionPurpose.Production && final.BatchId == fixture.Batch.Id && final.RecipeId == fixture.Version.Bundle.VersionId.ToString("D") && final.ProductionSequence == 1 &&
            final.ExecutionSessionId == original.ExecutionSessionId && final.OperatorId == ProbeFixture.Actor.Id &&
            final.OperatorName == original.OperatorName && final.RecipeJson == original.RecipeJson &&
            final.ProductId == original.ProductId && final.SampleId == fixture.Sample.SampleId && final.SourceKind == "Replay" &&
            final.StartedAt == original.StartedAt, "Restart preserves the original identity, batch/version, operator, source and allocated production number.");
        if (interrupted)
            Require(final.TestedImage is null && final.ReferenceImage is null && final.Defects.Count == 0 && final.DetectionMs is null,
                "Interrupted work is not automatically recaptured or reported as a quality decision.");
        else
        {
            Require(InspectionTransfer.Hash(final) == InspectionTransfer.Hash(original), "Completed archive remains byte-for-byte/hash identical after restart and ACK.");
            Require(final.TestedImage!.SequenceEqual(fixture.Tested) && final.ReferenceImage!.SequenceEqual(fixture.Reference) && final.Defects.Count > 0,
                "Completed result comes from the original real image pair.");
        }
        Require(finalPlc.AckAt is not null && store.ReadUnacknowledgedPlc() is null &&
            (plcBefore.AckAt is null || plcBefore.AckAt == finalPlc.AckAt), "ACK is durable and an already committed ACK time never changes.");
        var local = store.ReadRecent().Select(row => store.Get(row.Record.Id)!).ToArray();
        Require(local.Length == 2 && store.PendingCount() == 2 && store.ReadActiveBatch()!.NextProductionSequence == 2,
            "Recovery adds neither a duplicate inspection nor a production sequence and keeps both terminal records in the outbox.");
        var ledger = ReadLedger(folder);
        Require(ledger.Length == 1 && ControllerCount(folder, "Pending") == 0, "PLC counts exactly one result and finishes the original pending identity.");
        var wire = ledger.Single();
        Require(wire.GetProperty("sessionId").GetGuid() == identity.ControllerSessionId && wire.GetProperty("sequence").GetUInt32() == identity.TriggerSequence &&
            wire.GetProperty("inspectionId").GetGuid() == final.Id && wire.GetProperty("productionSequence").GetInt32() == 1 &&
            wire.GetProperty("resultCode").GetInt32() == (int)(interrupted ? PlcResultCode.NotEvaluated : PlcResultCode.Fail), "Real TCP ledger matches the recovered SQLite result.");
        var initialEvents = ReadEvents(folder, "initial"); var restoredEvents = ReadEvents(folder, "restored");
        Require(initialEvents.Count(row => row.GetProperty("kind").GetString() == "startedCommitted") == 1 &&
            initialEvents.Count(row => row.GetProperty("kind").GetString() == "captureCompleted") == (interrupted ? 0 : 1) &&
            !restoredEvents.Any(row => row.GetProperty("kind").GetString() is "inspectionDispatched" or "captureRequested"),
            "The new OS process never dispatches or captures the original physical trigger again.");
        using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "restored-ready.json")));
        Require(ready.RootElement.GetProperty("recoveredCount").GetInt32() == (interrupted ? 1 : 0), "Only a persisted Started record is recovered as Interrupted.");
        foreach (var record in local) await WriteAsync(folder, $"inspection-{record.Id}.json", record);
        var report = new { scope = ProbeFixture.Scope, scenario, startedAtUtc, completedAtUtc = DateTimeOffset.UtcNow,
            parentPid = Environment.ProcessId, port, killed, restoredExit, pythonExit, final.Id, final.ControllerSessionId, final.TriggerSequence,
            final.Purpose, final.ExecutionStatus, final.Decision, final.ProductionSequence, final.BatchId, final.RecipeId,
            contentHashBefore = InspectionTransfer.Hash(original), contentHashAfter = InspectionTransfer.Hash(final), plcAckBefore = plcBefore.AckAt,
            plcAckAfter = finalPlc.AckAt, initialCaptures = interrupted ? 0 : 1, restoredCaptures = 0, controllerResults = ledger.Length,
            plcPending = 0, stationRecords = local.Length, stationImages = local.Sum(record => (record.TestedImage is null ? 0 : 1) + (record.ReferenceImage is null ? 0 : 1)),
            outbox = store.PendingCount(), nextProductionSequence = store.ReadActiveBatch()!.NextProductionSequence, allChecksPassed = true };
        await WriteAsync(folder, "comparison.json", report);
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
    }

    internal static async Task RunStationAsync(string mode, string folder, int port)
    {
        var attempt = mode == "restored" ? "restored" : "initial";
        using var events = new StreamWriter(Path.Combine(folder, $"station-{attempt}.jsonl")) { AutoFlush = true };
        void Emit(string kind, object? value = null) => events.WriteLine(JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, pid = Environment.ProcessId, kind, value }, LineJson));
        var root = Directory.GetCurrentDirectory();
        var fixture = JsonSerializer.Deserialize<FixtureData>(await File.ReadAllTextAsync(Path.Combine(folder, "fixture.json")), Json)!;
        var store = new LocalInspectionStore(Path.Combine(folder, "station.db")); store.Initialize();
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        var recoveredCount = coordinator.RecoverInterrupted();
        var recipes = new LocalRecipeStore(store.DatabasePath); recipes.Initialize();
        coordinator.RestoreCachedBatchRecipe(recipes.Load(fixture.Batch.RecipeVersionId));
        // An old held identity can finish even though the restarted station has no current operator session.
        if (mode == "restored") coordinator.EndOperatorSession();
        await WriteAsync(folder, $"{attempt}-ready.json", new { atUtc = DateTimeOffset.UtcNow, pid = Environment.ProcessId, recoveredCount, active = store.ReadActiveBatch() });
        var runner = new PlcInspectionRunner(store, coordinator,
            () => store.ReadActiveBatch() is { Status: BatchStatus.InProgress, Session: { } session } active &&
                session.OperatorId == ProbeFixture.Actor.Id && session.ExpiresAt > DateTimeOffset.UtcNow && active.AcceptedProductionCount < fixture.Batch.PlannedQuantity,
            async (trigger, onStarted, token) =>
            {
                Emit("inspectionDispatched", trigger);
                Require(trigger.SampleId == fixture.Sample.SampleId, "Trigger sample belongs to the fixed local recipe.");
                return await coordinator.InspectAsync(InspectionPurpose.Production, fixture.Batch.StationId, trigger.ProductId, ProbeFixture.Actor,
                    new RecoverySource(new PublishedReplayImageSource(Path.Combine(root, "data"), fixture.Sample), mode == "kill-started", folder, Emit),
                    cancellationToken: token, identity: trigger.Identity, startedCommitted: record => { Emit("startedCommitted", record); onStarted(record); });
            }, new StatusProgress(status => Emit("status", status)));
        using var cancellation = new CancellationTokenSource();
        var running = runner.RunAsync("127.0.0.1", port, cancellation.Token);
        while (!File.Exists(Path.Combine(folder, $"{attempt}-stop")))
        {
            if (running.IsCompleted) { await running; throw new InvalidOperationException("Station runner stopped before the parent requested shutdown."); }
            await Task.Delay(30);
        }
        cancellation.Cancel();
        await running;
        Emit("stopped", new { coordinator.IsFaulted });
        Require(!coordinator.IsFaulted, "Recovered station stopped without a local fault.");
    }

    private static ProcessStartInfo StationStart(string root, string folder, int port, string mode)
    {
        var start = StartInfo(Path.Combine(root, ".local/dotnet/dotnet.exe"), root);
        foreach (var arg in new[] { typeof(RecoveryProbe).Assembly.Location, "station-child", mode, folder, port.ToString() }) start.ArgumentList.Add(arg);
        return start;
    }
    private static ProcessStartInfo PythonStart(string root, string folder, int port, string scenario)
    {
        var start = StartInfo(Path.Combine(root, ".venv/Scripts/python.exe"), root);
        foreach (var arg in new[] { Path.Combine(root, "tools/BoardTrace.Plc.Probe/recovery_scenarios.py"), "--scenario", "normal", "--host", "127.0.0.1",
            "--port", port.ToString(), "--count", "1", "--product-prefix", "SIM-PLC-RECOVERY", "--samples", Path.Combine(folder, "samples.jsonl"),
            "--state", Path.Combine(folder, "controller.db"), "--output", Path.Combine(folder, "simulator.jsonl"), "--timeout", "40" }) start.ArgumentList.Add(arg);
        start.Environment["BOARDTRACE_PLC_PROBE_FAULT"] = scenario;
        start.Environment["BOARDTRACE_PLC_PROBE_FOLDER"] = folder;
        return start;
    }
    private static ProcessStartInfo StartInfo(string executable, string root) => new(executable)
    { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    private static async Task WaitForFileAsync(string folder, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!File.Exists(Path.Combine(folder, name))) await Task.Delay(30, timeout.Token);
    }
    private static long ControllerCount(string folder, string table)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(folder, "controller.db")}"); db.Open();
        using var read = db.CreateCommand(); read.CommandText = $"SELECT COUNT(*) FROM {table};"; return (long)read.ExecuteScalar()!;
    }
    private static JsonElement[] ReadLedger(string folder)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(folder, "controller.db")}"); db.Open();
        using var read = db.CreateCommand(); read.CommandText = "SELECT ResultJson FROM Results;";
        using var rows = read.ExecuteReader(); var result = new List<JsonElement>();
        while (rows.Read()) result.Add(JsonSerializer.Deserialize<JsonElement>(rows.GetString(0))); return result.ToArray();
    }
    // A killed process can leave a partial final log line. Only complete emitted lines
    // are evidence; the actual SQLite state is checked independently above.
    private static JsonElement[] ReadEvents(string folder, string attempt) => File.ReadAllText(Path.Combine(folder, $"station-{attempt}.jsonl"))
        .Split('\n').SkipLast(1).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
    private static Task WriteAsync<T>(string folder, string name, T value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, Json));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record FixtureData(BatchDefinition Batch, ReplaySample Sample);
    private sealed class StatusProgress(Action<PlcStationStatus> emit) : IProgress<PlcStationStatus> { public void Report(PlcStationStatus value) => emit(value); }
    private sealed class RecoverySource(IImageSource source, bool block, string folder, Action<string, object?> emit) : IImageSource
    {
        public string SampleId => source.SampleId; public string SourceKind => source.SourceKind;
        public async Task<CapturedPair> CaptureAsync(CancellationToken token)
        {
            emit("captureRequested", null);
            if (block)
            {
                File.WriteAllText(Path.Combine(folder, "capture-blocked"), DateTimeOffset.UtcNow.ToString("O"));
                await Task.Delay(Timeout.Infinite, token);
            }
            var pair = await source.CaptureAsync(token);
            emit("captureCompleted", null);
            return pair;
        }
    }

    private sealed class OwnedProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly ProcessStartInfo start;
        private readonly string folder;
        private readonly string name;
        private readonly DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        private readonly Task<string> stdout;
        private readonly Task<string> stderr;
        private DateTimeOffset? killedAtUtc;
        private string? killReason;
        internal OwnedProcess(ProcessStartInfo start, string folder, string name)
        {
            this.start = start; this.folder = folder; this.name = name;
            process = Process.Start(start)!;
            stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
        }
        internal async Task<ProcessExit> KillAsync(string reason)
        {
            Require(!process.HasExited, "Owned station process must still be alive at the kill boundary.");
            killedAtUtc = DateTimeOffset.UtcNow; killReason = reason;
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return await SaveAsync();
        }
        internal async Task<ProcessExit> WaitSuccessAsync()
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var result = await SaveAsync();
            Require(result.ExitCode == 0, $"{name} failed; see its local stderr and event log."); return result;
        }
        private async Task<ProcessExit> SaveAsync()
        {
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".stdout.txt"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".stderr.txt"), await stderr);
            var result = new ProcessExit(process.Id, startedAtUtc, killedAtUtc, new DateTimeOffset(process.ExitTime).ToUniversalTime(),
                process.ExitCode, killReason, start.FileName, start.ArgumentList.ToArray());
            await WriteAsync(folder, name + "-process.json", result); return result;
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) await KillAsync("Owned-process cleanup after probe failure");
            else await SaveAsync();
            process.Dispose();
        }
    }
    private sealed record ProcessExit(int Pid, DateTimeOffset StartedAtUtc, DateTimeOffset? KilledAtUtc, DateTimeOffset ExitedAtUtc,
        int ExitCode, string? KillReason, string Executable, string[] Arguments);
}
