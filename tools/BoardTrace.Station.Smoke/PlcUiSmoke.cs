using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;

namespace BoardTrace.Station.Smoke;

public static partial class Program
{
    private static async Task VerifyPlcUiAsync(string output)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var source = JsonSerializer.Deserialize<ReplaySample>(File.ReadLines("training/manifests/inputs/validation.jsonl").First(), json)!;
        var reference = await File.ReadAllBytesAsync(Path.Combine("data", source.Reference));
        var tested = await File.ReadAllBytesAsync(Path.Combine("data", source.Image));
        var directory = Path.Combine(output, "plc-input");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "tested.jpg"), tested);
        var manifest = Path.Combine(directory, "inputs.jsonl");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new ReplaySample(source.SampleId, "tested.jpg", "reference-is-in-bundle.jpg")));
        var simulatorSamples = Path.Combine(directory, "simulator-samples.jsonl");
        await File.WriteAllTextAsync(simulatorSamples, JsonSerializer.Serialize(new { sampleId = source.SampleId }) + Environment.NewLine);
        await using var fixture = new BatchUiHttpFixture(source.SampleId, reference);
        var options = new StationOptions(fixture.Batch.StationId, directory, manifest, Path.Combine(output, "plc-station.db"),
            fixture.Address, Path.Combine(output, "plc-device.json"));
        await File.WriteAllTextAsync(options.CredentialsPath,
            JsonSerializer.Serialize(new StationCredentials("batch-device", BatchUiHttpFixture.Password)));
        var state = Path.Combine(output, "simulator-state.db");
        var events = Path.Combine(output, "simulator-events.jsonl");
        using var available = new TcpListener(IPAddress.Loopback, 0);
        available.Start();
        var port = ((IPEndPoint)available.LocalEndpoint).Port;
        available.Stop();

        var personnel = StationAuthentication.CreateClient(fixture.Address);
        var actor = await StationAuthentication.LoginOperatorAsync(personnel, new("batch-operator-1", BatchUiHttpFixture.Password));
        await using (var model = new StationViewModel(options, actor, personnel))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                ((Expander)window.FindName("BatchExpander")).IsExpanded = true;
                ((Expander)window.FindName("PlcExpander")).IsExpanded = true;
                await model.RefreshBatchesCommand.ExecuteAsync(null);
                await model.DownloadBatchCommand.ExecuteAsync(null);
                Require(model.RunCommand.CanExecute(null), model.BatchNotice);
                model.ProductId = "PLC-FIRST-FAIL";
                await model.RunCommand.ExecuteAsync(null);
                Require(model.Decision == "缺陷", "Real first article did not fail.");
                model.ConstructedNormal = true;
                model.ProductId = "PLC-FIRST-PASS";
                await model.RunCommand.ExecuteAsync(null);
                var pass = Guid.Parse(model.InspectionId);
                Require(model.Decision == "合格" && !model.RunCommand.CanExecute(null), "Passing first article did not wait for approval.");
                fixture.AllowUploads = true;
                await AwaitBatchUiAsync(() => fixture.Records.ContainsKey(pass), "first article fixture receipt");
                using (var quality = StationAuthentication.CreateClient(fixture.Address))
                {
                    using var login = await quality.PostAsJsonAsync("api/auth/login", new LoginRequest("batch-quality", BatchUiHttpFixture.Password));
                    login.EnsureSuccessStatusCode();
                    using var approval = await quality.PutAsJsonAsync($"api/batches/{fixture.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(pass));
                    approval.EnsureSuccessStatusCode();
                }
                await model.RefreshActiveBatchCommand.ExecuteAsync(null);
                Require(model.StartBatchCommand.CanExecute(null), model.BatchNotice);
                await model.StartBatchCommand.ExecuteAsync(null);
                Require(model.RunCommand.CanExecute(null), "Online production session was not established.");
                model.PlcHost = "127.0.0.1";
                model.PlcPort = port.ToString();
                await SnapshotAsync(window, Path.Combine(output, "20-plc-ready.png"));

                await RunScenarioAsync(model, "normal", 1, port, simulatorSamples, state, events, output);
                await CheckProductionAsync(model, options.DatabasePath, fixture, 1, source.SampleId, reference, tested);
                await SnapshotAsync(window, Path.Combine(output, "21-plc-normal.png"));
                await RunScenarioAsync(model, "busy", 2, port, simulatorSamples, state, events, output);
                await CheckProductionAsync(model, options.DatabasePath, fixture, 2, source.SampleId, reference, tested);
                await RunScenarioAsync(model, "duplicate-trigger", 3, port, simulatorSamples, state, events, output);
                await CheckProductionAsync(model, options.DatabasePath, fixture, 3, source.SampleId, reference, tested);

                using var withheld = StartSimulator("lost-ack", 4, port, simulatorSamples, state, events, output, 25);
                model.StartPlcCommand.Execute(null);
                try
                {
                    await AwaitBatchUiAsync(() => EventExists(events, "ackWithheld"), "PLC withheld ACK");
                    await AwaitBatchUiAsync(() => Production(new LocalInspectionStore(options.DatabasePath)).Length == 4, "fourth local result");
                    var fourth = Production(new LocalInspectionStore(options.DatabasePath)).Single(record => record.ProductionSequence == 4);
                    Require(new LocalInspectionStore(options.DatabasePath).ReadUnacknowledgedPlc()?.Record.Id == fourth.Id,
                        "Lost ACK did not leave the original committed inspection pending.");
                    await SnapshotAsync(window, Path.Combine(output, "22-plc-awaiting-ack.png"));
                }
                finally
                {
                    await model.StopPlcCommand.ExecuteAsync(null);
                    await StopSimulatorAsync(withheld, "lost-ack-before-restart", output);
                }
            }
            finally { window.Close(); }
        }

        var newPersonnel = StationAuthentication.CreateClient(fixture.Address);
        var sameActor = await StationAuthentication.LoginOperatorAsync(newPersonnel, new("batch-operator-1", BatchUiHttpFixture.Password));
        await using (var restarted = new StationViewModel(options, sameActor, newPersonnel))
        {
            var window = new MainWindow { DataContext = restarted };
            window.Show();
            try
            {
                await restarted.InitializeAsync();
                Require(restarted.ActiveBatchNumber == fixture.Batch.BatchNumber && restarted.ActiveRecipeIdentity == fixture.Version.Bundle.VersionId.ToString()
                    && !restarted.RunCommand.CanExecute(null), "Cold restart lost the fixed batch/recipe or inherited the old online start.");
                Require(new LocalInspectionStore(options.DatabasePath).ReadUnacknowledgedPlc() is { Record.ProductionSequence: 4 },
                    "Cold restart lost the pending PLC result.");
                ((Expander)window.FindName("PlcExpander")).IsExpanded = true;
                restarted.PlcHost = "127.0.0.1";
                restarted.PlcPort = port.ToString();
                await SnapshotAsync(window, Path.Combine(output, "23-plc-restarted-pending.png"));
                await RunScenarioAsync(restarted, "normal", 4, port, simulatorSamples, state, events, output);
                var store = new LocalInspectionStore(options.DatabasePath);
                Require(store.ReadUnacknowledgedPlc() is null && Production(store).Length == 4,
                    "Recovered ACK re-ran an image or left the original result pending.");
                await restarted.StartBatchCommand.ExecuteAsync(null);
                Require(restarted.RunCommand.CanExecute(null), "New process did not require and obtain its own online batch start.");
                await SnapshotAsync(window, Path.Combine(output, "24-plc-ack-recovered.png"));
                await RunScenarioAsync(restarted, "normal", 5, port, simulatorSamples, state, events, output);
                await CheckProductionAsync(restarted, options.DatabasePath, fixture, 5, source.SampleId, reference, tested);
                Require(!restarted.RunCommand.CanExecute(null) && restarted.BatchStateText.Contains("计划数量已接收完毕"),
                    "Final planned item unexpectedly left the batch ready for another inspection.");
                await SnapshotAsync(window, Path.Combine(output, "25-plc-plan-complete.png"));
                await AwaitBatchUiAsync(() => fixture.Records.Count == 7 && store.PendingCount() == 0, "all fixture uploads");
                var records = Production(store);
                Require(records.Select(record => record.ProductionSequence).Order().SequenceEqual(new int?[] { 1, 2, 3, 4, 5 })
                    && records.Select(record => (record.ControllerSessionId, record.TriggerSequence)).Distinct().Count() == 5,
                    "Production sequence or physical trigger identity duplicated.");
                var simulatorResults = File.ReadLines(events).Select(line => JsonDocument.Parse(line))
                    .Where(row => row.RootElement.GetProperty("kind").GetString() == "resultRead")
                    .Select(row => (Id: row.RootElement.GetProperty("inspectionId").GetGuid(),
                        Sequence: row.RootElement.GetProperty("productionSequence").GetInt32(),
                        ResultCode: row.RootElement.GetProperty("resultCode").GetInt32()))
                    .ToArray();
                Require(simulatorResults.Select(row => row.Id).Distinct().Count() == 5
                    && simulatorResults.All(row => row.ResultCode == 2 && records.Any(record => record.Id == row.Id && record.ProductionSequence == row.Sequence))
                    && EventExists(events, "ackWithheld") && EventExists(events, "scenarioVerified"),
                    "Simulator result identity/code differed from the five SQLite/WPF results or scenario events were absent.");
                foreach (var record in records)
                    Require(record.TestedImage!.SequenceEqual(tested) && record.ReferenceImage!.SequenceEqual(reference)
                        && fixture.Records.TryGetValue(record.Id, out var uploaded) && InspectionTransfer.Hash(uploaded) == InspectionTransfer.Hash(record),
                        "Real images or immutable fixture upload differ from local PLC result.");
                await File.WriteAllTextAsync(Path.Combine(output, "plc-ui-result.json"), JsonSerializer.Serialize(new
                {
                    scope = "Real WPF/Core/FluentModbus/PyModbus/SQLite + isolated loopback HTTP fixture; no SQL publication or quality approval",
                    options.DatabasePath, simulatorState = state, simulatorEvents = events,
                    fixture.Batch, fixture.Version.BundleHash,
                    pending = store.PendingCount(), productionCount = records.Length,
                    records = records.Select(record => new { record.Id, record.ProductionSequence, record.ControllerSessionId,
                        record.TriggerSequence, record.Decision, record.DetectionMs,
                        contentHash = InspectionTransfer.Hash(record), testedSha256 = BatchUiHash(record.TestedImage!),
                        referenceSha256 = BatchUiHash(record.ReferenceImage!) }),
                    eventsSha256 = BatchUiHash(await File.ReadAllBytesAsync(events))
                }, json));
            }
            finally { window.Close(); }
        }
    }

    private static InspectionRecord[] Production(LocalInspectionStore store) => store.ReadRecent()
        .Where(row => row.Record.Purpose == InspectionPurpose.Production)
        .Select(row => store.Get(row.Record.Id)!).ToArray();

    private static bool EventExists(string path, string kind)
    {
        if (!File.Exists(path)) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Contains($"\"kind\": \"{kind}\"", StringComparison.Ordinal);
    }

    private static async Task CheckProductionAsync(StationViewModel model, string database, BatchUiHttpFixture fixture,
        int count, string sampleId, byte[] reference, byte[] tested)
    {
        var store = new LocalInspectionStore(database);
        await AwaitBatchUiAsync(() => Production(store).Length == count && store.ReadUnacknowledgedPlc() is null,
            $"PLC result {count} and ACK");
        var records = Production(store);
        var latest = records.Single(record => record.ProductionSequence == count);
        Require(latest.SampleId == sampleId && latest.Decision == QualityDecision.Fail && latest.Defects.Count > 0
            && latest.TestedImage!.SequenceEqual(tested) && latest.ReferenceImage!.SequenceEqual(reference)
            && latest.ControllerSessionId is not null && latest.TriggerSequence is > 0,
            $"PLC result {count} did not preserve the real image, identity or detector boxes.");
        await AwaitBatchUiAsync(() => fixture.Records.ContainsKey(latest.Id), $"fixture upload {count}");
        Require(model.InspectionId == latest.Id.ToString() && model.DefectOverlays.Count > 0,
            "WPF did not show the current PLC inspection and real detection boxes.");
    }

    private static async Task RunScenarioAsync(StationViewModel model, string scenario, int count, int port,
        string samples, string state, string events, string output)
    {
        using var simulator = StartSimulator(scenario, count, port, samples, state, events, output);
        try
        {
            model.StartPlcCommand.Execute(null);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await simulator.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { throw new TimeoutException($"Simulator {scenario} did not finish; see {events}."); }
            var stderr = await simulator.StandardError.ReadToEndAsync();
            await File.WriteAllTextAsync(Path.Combine(output, $"simulator-{scenario}-{count}.stderr.log"), stderr);
            Require(simulator.ExitCode == 0, $"Simulator {scenario} failed: {stderr}");
        }
        finally
        {
            await model.StopPlcCommand.ExecuteAsync(null);
            if (!simulator.HasExited) simulator.Kill(entireProcessTree: true);
            await simulator.WaitForExitAsync();
        }
    }

    private static Process StartSimulator(string scenario, int count, int port, string samples, string state,
        string events, string output, int ackDelay = 3)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(".venv/Scripts/python.exe")) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetFullPath("."), RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "-m", "tools.simulator", "--scenario", scenario, "--host", "127.0.0.1",
                     "--port", port.ToString(), "--count", count.ToString(), "--product-id", "PLC-PRODUCTION",
                     "--samples", samples, "--state", state, "--output", events, "--timeout", "40",
                     "--ack-delay", ackDelay.ToString() }) start.ArgumentList.Add(value);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Python PLC simulator.");
        return process;
    }

    private static async Task StopSimulatorAsync(Process simulator, string name, string output)
    {
        if (!simulator.HasExited) simulator.Kill(entireProcessTree: true);
        await simulator.WaitForExitAsync();
        await File.WriteAllTextAsync(Path.Combine(output, $"simulator-{name}.stderr.log"), await simulator.StandardError.ReadToEndAsync());
    }
}
