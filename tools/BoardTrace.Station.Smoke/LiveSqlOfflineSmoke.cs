using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;

namespace BoardTrace.Station.Smoke;

public static partial class Program
{
    private sealed record LiveSqlContext(StationOptions Options, string AccountsPath, Guid BatchId, int InitialReceived,
        string? OperatorId, Guid? SessionId, string Output);
    private sealed record LiveSqlProduct(int ProcessId, Guid InspectionId, Guid SessionId, int Sequence,
        string ContentHash, string TestedSha256, string ReferenceSha256);
    private sealed record LiveInspectionDetail(InspectionRecord Inspection, DateTimeOffset ReceivedAt, string ContentHash);

    private static async Task VerifyLiveSqlAsync(string scope, string contextPath)
    {
        var context = await ReadLiveContext(contextPath);
        switch (scope)
        {
            case "live-sql-online": await LiveSqlOnline(context, contextPath); break;
            case "live-sql-offline": await LiveSqlOffline(context); break;
            case "live-sql-child": await LiveSqlChild(context); break;
            case "live-sql-verify": await LiveSqlVerify(context); break;
            default: throw new ArgumentException("Unsupported live SQL smoke phase: " + scope);
        }
    }

    private static readonly JsonSerializerOptions LiveJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static Task<LiveSqlContext> ReadLiveContext(string path) =>
        ReadLiveContextCore(path);
    private static async Task<LiveSqlContext> ReadLiveContextCore(string path) =>
        JsonSerializer.Deserialize<LiveSqlContext>(await File.ReadAllTextAsync(path), LiveJson)
        ?? throw new InvalidDataException("Missing isolated live SQL context.");
    private static Task WriteLive(string path, object value) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, LiveJson));
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<LoginWindow> OpenLiveLogin(StationOptions options, string accountsPath)
    {
        var accounts = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(accountsPath), LiveJson)!;
        var login = new LoginWindow(StationAuthentication.CreatePersonnelSession(options.ServerUrl), options);
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timeout.Tick += (_, _) => login.Close();
        _ = login.Dispatcher.InvokeAsync(() =>
        {
            ((TextBox)login.FindName("UserNameInput")).Text = "operator";
            ((PasswordBox)login.FindName("PasswordInput")).Password = accounts["operator"];
            ((Button)login.FindName("LoginButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, DispatcherPriority.Loaded);
        bool? accepted;
        try { timeout.Start(); accepted = login.ShowDialog(); }
        finally { timeout.Stop(); }
        Require(accepted == true && login.AuthenticatedUser is not null && login.PersonnelSession is not null
            && !login.ResumeBatch && ((PasswordBox)login.FindName("PasswordInput")).Password.Length == 0,
            "Real SQL LoginWindow did not authenticate the operator or clear PasswordBox.");
        return login;
    }

    private static async Task LiveSqlOnline(LiveSqlContext context, string contextPath)
    {
        var options = context.Options;
        var login = await OpenLiveLogin(options, context.AccountsPath);
        await using var model = new StationViewModel(options, login.AuthenticatedUser, login.PersonnelSession, false);
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await model.InitializeAsync();
            var details = await login.PersonnelSession!.Client.GetFromJsonAsync<BatchDetails>($"api/batches/{context.BatchId}");
            Require(details is { Status: BatchStatus.InProgress, Approval: not null }
                && details.Batch.StationId == options.StationId
                && details.Batch.PlannedQuantity - details.ReceivedProductionCount >= 2,
                "Real SQL fixture lacks an approved batch with two remaining products.");
            await model.RefreshBatchesCommand.ExecuteAsync(null);
            model.SelectedBatch = model.AssignedBatches.Single(row => row.Summary.Batch.Id == context.BatchId);
            await model.DownloadBatchCommand.ExecuteAsync(null);
            Require(model.ActiveBatchNumber == details!.Batch.BatchNumber && model.ActiveRecipeIdentity == details.Batch.RecipeVersionId.ToString()
                && model.StartBatchCommand.CanExecute(null),
                $"Real SQL bundle download did not load the approved fixed batch: {model.BatchNotice}; recipe={model.RecipeNotice}; active={model.ActiveBatchNumber}/{model.ActiveRecipeIdentity}; canStart={model.StartBatchCommand.CanExecute(null)}");
            await model.StartBatchCommand.ExecuteAsync(null);
            var active = new LocalInspectionStore(options.DatabasePath).ReadActiveBatch()!;
            Require(active.Session?.OperatorId == login.AuthenticatedUser!.Id && active.ArchiveId != Guid.Empty
                && active.Session.ArchiveId == active.ArchiveId && active.Session.ExpiresAt > DateTimeOffset.UtcNow
                && new LocalInspectionStore(options.DatabasePath).ReadBatchResume()?.ProtectedPayload is { Length: > 0 }
                && model.RunCommand.CanExecute(null), "Real SQL online start did not persist a usable bounded operator grant.");
            await SnapshotAsync(window, Path.Combine(context.Output, "01-real-sql-start.png"));
            await WriteLive(contextPath, context with { InitialReceived = details.ReceivedProductionCount,
                OperatorId = login.AuthenticatedUser!.Id, SessionId = active.Session!.Id });
        }
        finally { window.Close(); }
    }

    private static async Task LiveSqlOffline(LiveSqlContext context)
    {
        Require(context.SessionId is not null && context.OperatorId is not null, "Online start evidence was not saved.");
        var login = await OpenOfflineLoginAsync(context.Options, "ResumeButton");
        Require(login.ResumeBatch && login.AuthenticatedUser?.Id == context.OperatorId,
            "Real SQL operator's protected grant did not resume during central disconnect.");
        await using (var model = new StationViewModel(context.Options, login.AuthenticatedUser, login.PersonnelSession, true))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                Require(model.RunCommand.CanExecute(null), "A previously started real SQL batch was not ready offline.");
                model.ProductId = "SIM-REAL-SQL-OFFLINE-PARENT";
                await model.RunCommand.ExecuteAsync(null);
                var record = new LocalInspectionStore(context.Options.DatabasePath).Get(Guid.Parse(model.InspectionId))!;
                Require(record.Purpose == InspectionPurpose.Production && record.ProductionSequence == context.InitialReceived + 1
                    && record.ExecutionSessionId == context.SessionId && record.OperatorId == context.OperatorId
                    && record.ExecutionStatus == InspectionExecution.Completed && record.Decision == QualityDecision.Fail
                    && record.TestedImage is { Length: > 0 } && record.ReferenceImage is { Length: > 0 },
                    "First real SQL offline production did not save its actual image, identity and sequence.");
                await WriteLive(Path.Combine(context.Output, "parent-product.json"), Product(record));
                await SnapshotAsync(window, Path.Combine(context.Output, "02-real-sql-offline-parent.png"));
            }
            finally { window.Close(); }
        }
        var start = new ProcessStartInfo(Path.GetFullPath(".local/dotnet/dotnet.exe"))
        {
            WorkingDirectory = Path.GetFullPath("."), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--scope", "live-sql-child", "--context",
                     Path.Combine(context.Output, "live-context.json") }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start live SQL cold-resume WPF child.");
        var stdoutTask = child.StandardOutput.ReadToEndAsync();
        var stderrTask = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw new TimeoutException("Live SQL cold-resume WPF child exceeded 35 seconds; see child logs.");
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(context.Output, "child.stdout.log"), await stdoutTask);
            await File.WriteAllTextAsync(Path.Combine(context.Output, "child.stderr.log"), await stderrTask);
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Require(child.ExitCode == 0, "Live SQL cold-resume WPF child failed: " + stderr + stdout);
        var childProduct = JsonSerializer.Deserialize<LiveSqlProduct>(await File.ReadAllTextAsync(Path.Combine(context.Output, "child-product.json")), LiveJson)!;
        Require(childProduct.ProcessId == child.Id && childProduct.ProcessId != Environment.ProcessId
            && childProduct.SessionId == context.SessionId && childProduct.Sequence == context.InitialReceived + 2,
            "Independent WPF process changed the real SQL shift identity or sequence.");
        Require(new LocalInspectionStore(context.Options.DatabasePath).PendingCount() == 2,
            "Central disconnect did not leave exactly two complete local outbox records.");
    }

    private static async Task LiveSqlChild(LiveSqlContext context)
    {
        var login = await OpenOfflineLoginAsync(context.Options, "ResumeButton");
        Require(login.ResumeBatch && login.AuthenticatedUser?.Id == context.OperatorId,
            "Independent process could not decrypt original DPAPI/Cookie grant.");
        await using var model = new StationViewModel(context.Options, login.AuthenticatedUser, login.PersonnelSession, true);
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await model.InitializeAsync();
            Require(model.RunCommand.CanExecute(null), "Cold WPF process lost previously started real SQL batch.");
            model.ProductId = "SIM-REAL-SQL-OFFLINE-CHILD";
            await model.RunCommand.ExecuteAsync(null);
            var record = new LocalInspectionStore(context.Options.DatabasePath).Get(Guid.Parse(model.InspectionId))!;
            Require(record.Purpose == InspectionPurpose.Production && record.ProductionSequence == context.InitialReceived + 2
                && record.ExecutionSessionId == context.SessionId && record.OperatorId == context.OperatorId
                && record.ExecutionStatus == InspectionExecution.Completed && record.Decision == QualityDecision.Fail
                && record.TestedImage is { Length: > 0 } && record.ReferenceImage is { Length: > 0 },
                "Cold WPF process did not save its own real image and immutable batch identity.");
            await WriteLive(Path.Combine(context.Output, "child-product.json"), Product(record));
            await SnapshotAsync(window, Path.Combine(context.Output, "03-real-sql-cold-child.png"));
        }
        finally { window.Close(); }
    }

    private static LiveSqlProduct Product(InspectionRecord record) => new(Environment.ProcessId, record.Id,
        record.ExecutionSessionId!.Value, record.ProductionSequence!.Value, InspectionTransfer.Hash(record),
        Sha(record.TestedImage!), Sha(record.ReferenceImage!));

    private static async Task LiveSqlVerify(LiveSqlContext context)
    {
        var store = new LocalInspectionStore(context.Options.DatabasePath);
        var ids = new[] { "parent-product.json", "child-product.json" }.Select(name =>
            JsonSerializer.Deserialize<LiveSqlProduct>(File.ReadAllText(Path.Combine(context.Output, name)), LiveJson)!).ToArray();
        Require(ids.Select(item => item.InspectionId).Distinct().Count() == 2 && store.PendingCount() == 2,
            "Offline originals were not intact before reconnect upload.");
        await using (var model = new StationViewModel(context.Options, null, null, false))
        {
            var window = new MainWindow { DataContext = model };
            window.Show();
            try
            {
                await model.InitializeAsync();
                await AwaitBatchUiAsync(() => store.PendingCount() == 0, "real SQL offline upload receipts");
                Require(!model.RunCommand.CanExecute(null), "Anonymous upload recovery gained production authority.");
                await SnapshotAsync(window, Path.Combine(context.Output, "04-real-sql-uploaded.png"));
            }
            finally { window.Close(); }
        }
        var accounts = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(context.AccountsPath), LiveJson)!;
        using var quality = StationAuthentication.CreateClient(context.Options.ServerUrl);
        using (var login = await quality.PostAsJsonAsync("api/auth/login", new LoginRequest("quality", accounts["quality"])))
            login.EnsureSuccessStatusCode();
        foreach (var item in ids)
        {
            var local = store.Get(item.InspectionId)!;
            var archive = store.ReadArchive(item.InspectionId)!;
            var detail = await quality.GetFromJsonAsync<LiveInspectionDetail>($"api/inspections/{item.InspectionId}")
                ?? throw new InvalidDataException("Central SQL did not return uploaded inspection.");
            var tested = await quality.GetByteArrayAsync($"api/inspections/{item.InspectionId}/images/tested");
            var reference = await quality.GetByteArrayAsync($"api/inspections/{item.InspectionId}/images/reference");
            Require(local.Id == detail.Inspection.Id && local.Purpose == InspectionPurpose.Production
                && local.BatchId == context.BatchId && local.ExecutionSessionId == context.SessionId
                && detail.Inspection.ExecutionSessionId == context.SessionId && detail.Inspection.ProductionSequence == item.Sequence
                && detail.ContentHash == item.ContentHash && archive.Receipt?.ContentHash == item.ContentHash
                && InspectionTransfer.Hash(local) == item.ContentHash
                && InspectionTransfer.Hash(detail.Inspection with { TestedImage = tested, ReferenceImage = reference }) == item.ContentHash
                && Sha(tested) == item.TestedSha256 && Sha(reference) == item.ReferenceSha256
                && tested.SequenceEqual(local.TestedImage!) && reference.SequenceEqual(local.ReferenceImage!),
                "Real SQL metadata, original images, content hash or local receipt differ after reconnect.");
            await WriteLive(Path.Combine(context.Output, $"central-{item.InspectionId}.json"), detail);
        }
        var batch = await quality.GetFromJsonAsync<BatchDetails>($"api/batches/{context.BatchId}");
        Require(batch is not null && batch.ReceivedProductionCount == context.InitialReceived + 2
            && batch.ReceivedProductionCount <= batch.Batch.PlannedQuantity
            && store.ReadRecent().Count == batch.FirstArticles.Count + batch.ReceivedProductionCount,
            "Real SQL batch count duplicated or exceeded its planned quantity.");
        await WriteLive(Path.Combine(context.Output, "final-result.json"), new
        {
            kind = "isolated real SQL 5181 fixture, seeded publication is not quality approved",
            context.BatchId, context.SessionId, context.InitialReceived, finalReceived = batch!.ReceivedProductionCount,
            localPending = store.PendingCount(), inspectionIds = ids.Select(item => item.InspectionId).ToArray(),
            localDatabase = context.Options.DatabasePath, server = context.Options.ServerUrl,
            checks = new[] { "real LoginWindow and SQL Cookie", "real approved batch download and bounded start",
                "two real image detections while central stopped", "independent C# WPF process DPAPI resume",
                "device upload to same SQL database after restart", "exact per-ID metadata/image/hash/receipt", "no duplicated production count" }
        });
    }
}
