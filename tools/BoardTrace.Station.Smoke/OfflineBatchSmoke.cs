using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Smoke;

public static partial class Program
{
    private sealed record OfflineChildContext(StationOptions Options, Guid ExpectedSessionId, string ExpectedOperatorId, string ResultPath);
    private sealed record OfflineChildResult(int ProcessId, Guid SessionId, Guid InspectionId, int ProductionSequence, string OperatorId);

    private static async Task VerifyOfflineBatchAsync(string output)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var source = JsonSerializer.Deserialize<ReplaySample>(File.ReadLines("training/manifests/inputs/validation.jsonl").First(), json)!;
        var reference = await File.ReadAllBytesAsync(Path.Combine("data", source.Reference));
        var tested = await File.ReadAllBytesAsync(Path.Combine("data", source.Image));
        var input = Path.Combine(output, "offline-input");
        Directory.CreateDirectory(input);
        await File.WriteAllBytesAsync(Path.Combine(input, "tested.jpg"), tested);
        var manifest = Path.Combine(input, "inputs.jsonl");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new ReplaySample(source.SampleId, "tested.jpg", "reference-in-bundle.jpg")));
        await using var fixture = new BatchUiHttpFixture(source.SampleId, reference);
        var options = new StationOptions(fixture.Batch.StationId, input, manifest, Path.Combine(output, "offline-station.db"),
            fixture.Address, Path.Combine(output, "offline-device.json"));
        await File.WriteAllTextAsync(options.CredentialsPath,
            JsonSerializer.Serialize(new StationCredentials("batch-device", BatchUiHttpFixture.Password)));
        var personnel = StationAuthentication.CreatePersonnelSession(fixture.Address);
        var actor = await StationAuthentication.LoginOperatorAsync(personnel.Client, new("batch-operator-1", BatchUiHttpFixture.Password));
        Guid firstArticle;
        Guid firstProduction;
        await using (var model = new StationViewModel(options, actor, personnel, false))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                await model.RefreshBatchesCommand.ExecuteAsync(null);
                await model.DownloadBatchCommand.ExecuteAsync(null);
                Require(model.RunCommand.CanExecute(null), model.BatchNotice);
                model.ProductId = "OFFLINE-FIRST-FAIL";
                await model.RunCommand.ExecuteAsync(null);
                Require(model.Decision == "缺陷", "Real first article did not fail in the offline fixture.");
                model.ConstructedNormal = true;
                model.ProductId = "OFFLINE-FIRST-PASS";
                await model.RunCommand.ExecuteAsync(null);
                firstArticle = Guid.Parse(model.InspectionId);
                fixture.AllowUploads = true;
                await AwaitBatchUiAsync(() => fixture.Records.ContainsKey(firstArticle), "first article receipt before offline start");
                using (var quality = StationAuthentication.CreateClient(fixture.Address))
                {
                    using var login = await quality.PostAsJsonAsync("api/auth/login", new LoginRequest("batch-quality", BatchUiHttpFixture.Password));
                    login.EnsureSuccessStatusCode();
                    using var approval = await quality.PutAsJsonAsync($"api/batches/{fixture.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(firstArticle));
                    approval.EnsureSuccessStatusCode();
                }
                await model.RefreshActiveBatchCommand.ExecuteAsync(null);
                await model.StartBatchCommand.ExecuteAsync(null);
                Require(model.RunCommand.CanExecute(null), "Online start did not grant bounded production.");
                var active = new LocalInspectionStore(options.DatabasePath).ReadActiveBatch()!;
                Require(active.Session?.OperatorId == actor.Id && active.ArchiveId != Guid.Empty
                    && active.Session.ArchiveId == active.ArchiveId
                    && new LocalInspectionStore(options.DatabasePath).ReadBatchResume()?.ProtectedPayload is { Length: > 0 },
                    "Online start did not persist the original protected personnel session.");
                await SnapshotAsync(window, Path.Combine(output, "01-online-start.png"));
                fixture.Mode = BatchUiNetwork.Disconnected;
                model.ConstructedNormal = false;
                model.ProductId = "OFFLINE-FIRST-PRODUCTION";
                await model.RunCommand.ExecuteAsync(null);
                firstProduction = Guid.Parse(model.InspectionId);
                Require(new LocalInspectionStore(options.DatabasePath).Get(firstProduction) is { Purpose: InspectionPurpose.Production, ProductionSequence: 1 },
                    "Previously started batch did not accept its first offline product.");
            }
            finally { window.Close(); }
        }

        var originalSessionId = new LocalInspectionStore(options.DatabasePath).ReadActiveBatch()!.Session!.Id;
        var childContextPath = Path.Combine(output, "offline-child-context.json");
        var childResultPath = Path.Combine(output, "offline-child-result.json");
        await File.WriteAllTextAsync(childContextPath,
            JsonSerializer.Serialize(new OfflineChildContext(options, originalSessionId, actor.Id, childResultPath), json));
        var start = new ProcessStartInfo(Path.GetFullPath(".local/dotnet/dotnet.exe"))
        {
            WorkingDirectory = Path.GetFullPath("."), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--scope", "offline-resume-child", "--context", childContextPath })
            start.ArgumentList.Add(argument);
        using (var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the independent WPF resume process."))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await child.WaitForExitAsync(deadline.Token);
            var stdout = await child.StandardOutput.ReadToEndAsync();
            var stderr = await child.StandardError.ReadToEndAsync();
            await File.WriteAllTextAsync(Path.Combine(output, "offline-child.stdout.log"), stdout);
            await File.WriteAllTextAsync(Path.Combine(output, "offline-child.stderr.log"), stderr);
            Require(child.ExitCode == 0, "Independent WPF resume process failed: " + stderr + stdout);
            var result = JsonSerializer.Deserialize<OfflineChildResult>(await File.ReadAllTextAsync(childResultPath), json)!;
            Require(result.ProcessId == child.Id && child.Id != Environment.ProcessId && result.SessionId == originalSessionId
                && result.OperatorId == actor.Id && result.ProductionSequence == 2,
                "Cold process did not keep the original operator/session and second production sequence.");
        }

        var restored = await OpenOfflineLoginAsync(options, "ResumeButton");
        Require(restored.AuthenticatedUser?.Id == actor.Id && restored.ResumeBatch && restored.PersonnelSession is not null,
            "The actual login window did not restore the original operator and session.");
        await using (var model = new StationViewModel(options, restored.AuthenticatedUser, restored.PersonnelSession, restored.ResumeBatch))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                Require(model.RunCommand.CanExecute(null) && model.ActiveBatchNumber == fixture.Batch.BatchNumber,
                    "Offline restart did not resume the approved, already started batch.");
                model.ConstructedNormal = true;
                model.ProductId = "OFFLINE-RESTORED-PRODUCTION";
                await model.RunCommand.ExecuteAsync(null);
                var secondProduction = new LocalInspectionStore(options.DatabasePath).Get(Guid.Parse(model.InspectionId))!;
                Require(secondProduction.Purpose == InspectionPurpose.Production && secondProduction.ProductionSequence == 3
                    && secondProduction.OperatorId == actor.Id && secondProduction.TestedImage!.SequenceEqual(reference),
                    "Offline continuation lost source evidence, operator identity, or durable sequence.");
                await SnapshotAsync(window, Path.Combine(output, "02-restored-production.png"));

                fixture.Mode = BatchUiNetwork.Online;
                await AwaitBatchUiAsync(() => model.PendingCount == 0 && fixture.Records.ContainsKey(secondProduction.Id), "offline records uploaded after reconnect");
                Require(InspectionTransfer.Hash(fixture.Records[firstProduction]) == InspectionTransfer.Hash(new LocalInspectionStore(options.DatabasePath).Get(firstProduction)!)
                    && InspectionTransfer.Hash(fixture.Records[secondProduction.Id]) == InspectionTransfer.Hash(secondProduction),
                    "Reconnect upload changed an offline original.");
                fixture.Mode = BatchUiNetwork.Unauthorized;
                model.ProductId = "MUST-NOT-ACCEPT-AFTER-401";
                await model.RunCommand.ExecuteAsync(null);
                Require(!model.RunCommand.CanExecute(null) && new LocalInspectionStore(options.DatabasePath).ReadBatchResume() is null,
                    "Explicit personnel 401 retained production authority or the protected resume ticket.");
            }
            finally { window.Close(); }
        }

        fixture.Mode = BatchUiNetwork.Disconnected;
        var refused = new LoginWindow(StationAuthentication.CreatePersonnelSession(options.ServerUrl), options);
        refused.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(!((Button)refused.FindName("ResumeButton")).IsEnabled, "A revoked ticket was offered for offline recovery.");
        refused.Close();

        // Start a new bounded online shift, then prove that even a broken manifest
        // cannot leave this earlier operator's cookie/session available for resume.
        fixture.Mode = BatchUiNetwork.Online;
        var replacementPersonnel = StationAuthentication.CreatePersonnelSession(fixture.Address);
        var replacement = await StationAuthentication.LoginOperatorAsync(replacementPersonnel.Client,
            new("batch-operator-2", BatchUiHttpFixture.Password));
        await using (var model = new StationViewModel(options, replacement, replacementPersonnel, false))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                Require(model.StartBatchCommand.CanExecute(null), "New shift could not start the already approved batch.");
                await model.StartBatchCommand.ExecuteAsync(null);
                Require(new LocalInspectionStore(options.DatabasePath).ReadBatchResume()?.ProtectedPayload is { Length: > 0 },
                    "New online start did not persist a bounded recovery ticket.");
            }
            finally { window.Close(); }
        }

        var archiveOptions = options with { ManifestPath = Path.Combine(input, "missing-manifest.jsonl") };
        var anotherPersonnel = StationAuthentication.CreatePersonnelSession(fixture.Address);
        var another = await StationAuthentication.LoginOperatorAsync(anotherPersonnel.Client,
            new("batch-operator-1", BatchUiHttpFixture.Password));
        await using (var model = new StationViewModel(archiveOptions, another, anotherPersonnel, false))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                var state = new LocalInspectionStore(options.DatabasePath);
                Require(state.ReadActiveBatch()?.Session is null && state.ReadBatchResume() is null && !model.RunCommand.CanExecute(null),
                    "Fresh online login kept the previous shift's ticket after manifest initialization failed.");
            }
            finally { window.Close(); }
        }
        fixture.Mode = BatchUiNetwork.Disconnected;
        var afterBadManifest = new LoginWindow(StationAuthentication.CreatePersonnelSession(options.ServerUrl), archiveOptions);
        afterBadManifest.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(!((Button)afterBadManifest.FindName("ResumeButton")).IsEnabled,
            "Missing manifest on fresh login preserved a recoverable old operator ticket.");
        afterBadManifest.Close();

        var archive = await OpenOfflineLoginAsync(archiveOptions, "RecoveryButton");
        Require(archive.AuthenticatedUser is null && archive.PersonnelSession is null, "Archive recovery created a personnel identity.");
        await using (var model = new StationViewModel(archiveOptions, null, null, false))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                Require(model.StartPlcCommand.CanExecute(null) && !model.RunCommand.CanExecute(null) && !model.CanEdit,
                    "Missing manifest blocked PLC ACK/archive recovery or enabled new inspection.");
                model.SelectedHistory = model.History.Single(row => row.Id == firstProduction);
                Require(model.ViewHistoryCommand.CanExecute(null), "Archive-only mode did not allow opening a stored inspection.");
                await model.ViewHistoryCommand.ExecuteAsync(null);
                var original = new LocalInspectionStore(options.DatabasePath).Get(firstProduction)!;
                Require(model.InspectionId == firstProduction.ToString() && model.TestedImage is not null && model.ReferenceImage is not null
                    && model.Decision == StationViewModel.DecisionLabel(original.Decision) && model.DefectOverlays.Count == original.Defects.Count,
                    "Archive-only mode did not display the original image pair, defects, and decision.");
                await SnapshotAsync(window, Path.Combine(output, "03-archive-only.png"));

                using var connection = new SqliteConnection($"Data Source={options.DatabasePath}");
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TRIGGER refuse_shift_clear BEFORE UPDATE ON ActiveBatch BEGIN SELECT RAISE(ABORT, 'smoke: clear failed'); END;";
                    command.ExecuteNonQuery();
                }
                using var rejectedPersonnel = StationAuthentication.CreatePersonnelSession(fixture.Address);
                var rejected = false;
                try { model.SignIn(actor, rejectedPersonnel, false); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && model.OperatorName == "未登录" && !model.RunCommand.CanExecute(null),
                    "A failed local session clear allowed a new operator to bypass the fault.");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DROP TRIGGER refuse_shift_clear;";
                    command.ExecuteNonQuery();
                }
            }
            finally { window.Close(); }
        }
        var store = new LocalInspectionStore(options.DatabasePath);
        var beforePurge = store.Get(firstProduction)!;
        var originalReceipt = store.ReadArchive(firstProduction)!.Receipt!;
        var beforeCount = store.ReadRecent().Count;
        using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE UploadReceipts SET AcknowledgedAt=$old WHERE InspectionId=$id;";
            command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", firstProduction.ToString());
            Require(command.ExecuteNonQuery() == 1, "Fixture could not backdate the local acknowledgment for retention testing.");
        }
        fixture.Mode = BatchUiNetwork.Online;
        await using (var model = new StationViewModel(archiveOptions, null, null, false))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                await AwaitBatchUiAsync(() => store.ReadArchive(firstProduction)?.ImagesPurgedAt is not null,
                    "natural upload-loop image retention cleanup");
                var retained = store.ReadArchive(firstProduction)!;
                Require(retained.Record.Id == beforePurge.Id && retained.Record.Decision == beforePurge.Decision
                    && retained.Record.Defects.Count == beforePurge.Defects.Count && retained.Receipt == originalReceipt
                    && retained.Record.TestedImage is null && retained.Record.ReferenceImage is null && store.PendingCount() == 0
                    && store.ReadRecent().Count == beforeCount && !model.RunCommand.CanExecute(null),
                    "Retention cleanup lost original metadata/receipt, left image BLOBs, or accepted a new inspection.");
                model.SelectedHistory = model.History.Single(row => row.Id == firstProduction);
                Require(model.ViewHistoryCommand.CanExecute(null), "Purged archive was not selectable without personnel login.");
                await model.ViewHistoryCommand.ExecuteAsync(null);
                Require(model.InspectionId == firstProduction.ToString() && model.Decision == StationViewModel.DecisionLabel(beforePurge.Decision)
                    && model.DefectCount == beforePurge.Defects.Count.ToString() && model.TestedImage is null && model.ReferenceImage is null
                    && model.DefectOverlays.Count == 0 && model.Notice.Contains("中央追溯", StringComparison.Ordinal),
                    "WPF did not show the preserved verdict/defect count with empty images and central trace notice.");
                await SnapshotAsync(window, Path.Combine(output, "04-retained-archive.png"));
            }
            finally { window.Close(); }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "offline-result.json"), JsonSerializer.Serialize(new
        {
            fixtureKind = "isolated HTTP batch fixture, not a quality-approved SQL publication",
            firstArticle, firstProduction, records = store.ReadRecent().Count,
            pending = store.PendingCount(), received = fixture.Records.Keys.Order().ToArray(),
            imagePurgedAt = store.ReadArchive(firstProduction)!.ImagesPurgedAt,
            checks = new[] { "independent WPF process DPAPI and Cookie resume", "same bounded operator session", "real image production during disconnect",
                "reconnect uploads original bytes", "401 revokes local resume", "fresh login clears old session before a missing manifest fails",
                "archive-only reads original image pair and verdict", "failed local clear cannot SignIn", "archive-only PLC start remains available without production authority",
                "natural upload-loop retention removes only aged acknowledged BLOBs and WPF preserves the archived verdict" }
        }, json));
    }

    private static async Task VerifyOfflineResumeChildAsync(string contextPath)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var context = JsonSerializer.Deserialize<OfflineChildContext>(await File.ReadAllTextAsync(contextPath), json)!;
        var login = await OpenOfflineLoginAsync(context.Options, "ResumeButton");
        Require(login.ResumeBatch && login.AuthenticatedUser?.Id == context.ExpectedOperatorId,
            "A separate process did not decrypt and accept the protected operator ticket.");
        await using var model = new StationViewModel(context.Options, login.AuthenticatedUser, login.PersonnelSession, login.ResumeBatch);
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await model.InitializeAsync();
            var before = new LocalInspectionStore(context.Options.DatabasePath).ReadActiveBatch()!;
            Require(before.Session?.Id == context.ExpectedSessionId && before.Session.ArchiveId == before.ArchiveId
                && model.RunCommand.CanExecute(null),
                "Cold process reset the shift session or could not accept production.");
            model.ProductId = "OFFLINE-COLD-PROCESS";
            await model.RunCommand.ExecuteAsync(null);
            var record = new LocalInspectionStore(context.Options.DatabasePath).Get(Guid.Parse(model.InspectionId))!;
            Require(record.Purpose == InspectionPurpose.Production && record.ProductionSequence == 2
                && record.ExecutionSessionId == context.ExpectedSessionId && record.OperatorId == context.ExpectedOperatorId
                && record.TestedImage is { Length: > 0 } && record.ReferenceImage is { Length: > 0 },
                "Independent process did not durably save the second real production image and result.");
            await File.WriteAllTextAsync(context.ResultPath, JsonSerializer.Serialize(new OfflineChildResult(
                Environment.ProcessId, context.ExpectedSessionId, record.Id, record.ProductionSequence!.Value, record.OperatorId), json));
        }
        finally { window.Close(); }
    }

    private static async Task<LoginWindow> OpenOfflineLoginAsync(StationOptions options, string buttonName)
    {
        var login = new LoginWindow(StationAuthentication.CreatePersonnelSession(options.ServerUrl), options);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timeout.Tick += (_, _) => login.Close();
        _ = login.Dispatcher.InvokeAsync(async () =>
        {
            var button = (Button)login.FindName(buttonName);
            for (var attempt = 0; !button.IsEnabled && attempt < 100; attempt++) await Task.Delay(20);
            if (button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, DispatcherPriority.Loaded);
        bool? accepted;
        try { timeout.Start(); accepted = login.ShowDialog(); }
        finally { timeout.Stop(); }
        Require(accepted == true, $"LoginWindow {buttonName} was not accepted: {((TextBlock)login.FindName("Feedback")).Text}");
        return login;
    }
}
