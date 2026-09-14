using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Smoke;

public static partial class Program
{
    private static async Task VerifyBatchUiAsync(string output, StationOptions defaults)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var source = JsonSerializer.Deserialize<ReplaySample>(File.ReadLines(defaults.ManifestPath).First(), json)!;
        var reference = await File.ReadAllBytesAsync(Path.Combine(defaults.DataRoot, source.Reference));
        var tested = await File.ReadAllBytesAsync(Path.Combine(defaults.DataRoot, source.Image));
        var directory = Path.Combine(output, "batch-input");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "tested.jpg"), tested);
        var manifest = Path.Combine(directory, "inputs.jsonl");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new ReplaySample(source.SampleId, "tested.jpg", "not-on-disk.jpg")));
        await using var fixture = new BatchUiHttpFixture(source.SampleId, reference);
        var options = defaults with { StationId = fixture.Batch.StationId, ServerUrl = fixture.Address,
            DataRoot = directory, ManifestPath = manifest, DatabasePath = Path.Combine(output, "batch-ui.db"),
            CredentialsPath = Path.Combine(output, "batch-ui-device.json") };
        await File.WriteAllTextAsync(options.CredentialsPath, JsonSerializer.Serialize(new StationCredentials("batch-device", BatchUiHttpFixture.Password)));
        var personnel = StationAuthentication.CreatePersonnelSession(fixture.Address);
        var actor = await StationAuthentication.LoginOperatorAsync(personnel.Client, new("batch-operator-1", BatchUiHttpFixture.Password));
        await using var model = new StationViewModel(options, actor, personnel, false);
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await model.InitializeAsync();
            ((Expander)window.FindName("RecipeExpander")).IsExpanded = false;
            ((Expander)window.FindName("BatchExpander")).IsExpanded = true;
            await model.RefreshBatchesCommand.ExecuteAsync(null);
            Require(model.AssignedBatches.Count == 1 && model.DownloadBatchCommand.CanExecute(null), model.BatchNotice);
            await model.DownloadBatchCommand.ExecuteAsync(null);
            var store = new LocalInspectionStore(options.DatabasePath);
            Require(model.ActiveBatchNumber == fixture.Batch.BatchNumber && model.ActiveRecipeIdentity == fixture.Version.Bundle.VersionId.ToString()
                && model.RunButtonText == "执行首件检测" && model.RunCommand.CanExecute(null), "Full batch download did not enable the first article: " + model.BatchNotice);
            Require(!File.Exists(Path.Combine(directory, "not-on-disk.jpg")), "Fixture unexpectedly had an on-disk reference.");
            await SnapshotAsync(window, Path.Combine(output, "10-batch-downloaded.png"));

            model.ProductId = "SIM-BATCH-FIRST-FAIL";
            await model.RunCommand.ExecuteAsync(null);
            var fail = store.Get(Guid.Parse(model.InspectionId))!;
            Require(fail.Purpose == InspectionPurpose.FirstArticle && fail.Decision == QualityDecision.Fail && model.RunCommand.CanExecute(null),
                "A real defect first article did not remain retryable.");
            await SnapshotAsync(window, Path.Combine(output, "11-batch-first-fail.png"));
            model.ConstructedNormal = true;
            model.ProductId = "SIM-BATCH-FIRST-PASS";
            await model.RunCommand.ExecuteAsync(null);
            var pass = store.Get(Guid.Parse(model.InspectionId))!;
            Require(pass.Decision == QualityDecision.Pass && pass.SourceKind == "ConstructedNormal" && pass.TestedImage!.SequenceEqual(reference)
                && pass.ReferenceImage!.SequenceEqual(reference) && model.PendingCount == 2 && !model.RunCommand.CanExecute(null)
                && model.BatchStateText.Contains("等待中央接收"), "Passing first article did not pause for a real receipt.");
            await SnapshotAsync(window, Path.Combine(output, "12-batch-pass-pending.png"));
            await model.RunCommand.ExecuteAsync(null);
            Require(store.ReadRecent().Count == 2 && !model.RunCommand.CanExecute(null), "An extra first article was accepted while Pass awaited receipt.");
            fixture.AllowUploads = true;
            await AwaitBatchUiAsync(() => model.PendingCount == 0 && model.BatchStateText.Contains("等待质量批准"), "first-article acknowledgments");
            using (var quality = StationAuthentication.CreateClient(fixture.Address))
            {
                using var login = await quality.PostAsJsonAsync("api/auth/login", new LoginRequest("batch-quality", BatchUiHttpFixture.Password));
                login.EnsureSuccessStatusCode();
                using var approval = await quality.PutAsJsonAsync($"api/batches/{fixture.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(pass.Id));
                approval.EnsureSuccessStatusCode();
            }
            await model.RefreshActiveBatchCommand.ExecuteAsync(null);
            Require(model.StartBatchCommand.CanExecute(null) && !model.RunCommand.CanExecute(null) && model.BatchStateText.Contains("等待本班在线启动"),
                "Approval refresh skipped the separate online start gate.");
            await model.StartBatchCommand.ExecuteAsync(null);
            var firstSession = store.ReadActiveBatch()!.Session!;
            Require(firstSession.OperatorId == actor.Id && firstSession.ArchiveId == store.ReadActiveBatch()!.ArchiveId
                && model.RunCommand.CanExecute(null) && model.RunButtonText == "检测生产件", model.BatchNotice);

            var emptyOptions = options with { DatabasePath = Path.Combine(output, "batch-ui-second-empty.db") };
            var emptyPersonnel = StationAuthentication.CreatePersonnelSession(fixture.Address);
            var emptyActor = await StationAuthentication.LoginOperatorAsync(emptyPersonnel.Client,
                new("batch-operator-1", BatchUiHttpFixture.Password));
            await using (var emptyModel = new StationViewModel(emptyOptions, emptyActor, emptyPersonnel, false))
            {
                await emptyModel.InitializeAsync();
                await emptyModel.RefreshBatchesCommand.ExecuteAsync(null);
                await emptyModel.DownloadBatchCommand.ExecuteAsync(null);
                Require(emptyModel.StartBatchCommand.CanExecute(null), "Second empty archive could not reach the real start request.");
                await emptyModel.StartBatchCommand.ExecuteAsync(null);
                var emptyStore = new LocalInspectionStore(emptyOptions.DatabasePath);
                Require(emptyStore.ReadActiveBatch() is { Session: null } emptyBatch
                    && emptyBatch.ArchiveId != firstSession.ArchiveId && !emptyModel.RunCommand.CanExecute(null)
                    && emptyStore.ReadRecent().Count == 0 && fixture.BoundArchiveId == firstSession.ArchiveId,
                    "Different empty local archive bypassed the bound batch start or created a Started record.");
            }
            var backupOptions = options with { DatabasePath = Path.Combine(output, "batch-ui-backed-up.db") };
            using (var sourceDb = new SqliteConnection($"Data Source={options.DatabasePath}"))
            using (var backupDb = new SqliteConnection($"Data Source={backupOptions.DatabasePath}"))
            {
                sourceDb.Open(); backupDb.Open(); sourceDb.BackupDatabase(backupDb);
            }
            var backupPersonnel = StationAuthentication.CreatePersonnelSession(fixture.Address);
            var backupActor = await StationAuthentication.LoginOperatorAsync(backupPersonnel.Client,
                new("batch-operator-1", BatchUiHttpFixture.Password));
            await using (var backupModel = new StationViewModel(backupOptions, backupActor, backupPersonnel, false))
            {
                await backupModel.InitializeAsync();
                Require(new LocalInspectionStore(backupOptions.DatabasePath).ReadActiveBatch()?.ArchiveId == firstSession.ArchiveId
                    && backupModel.StartBatchCommand.CanExecute(null), "SQLite backup lost the bound local archive identity.");
                await backupModel.StartBatchCommand.ExecuteAsync(null);
                Require(new LocalInspectionStore(backupOptions.DatabasePath).ReadActiveBatch()?.Session?.ArchiveId == firstSession.ArchiveId
                    && backupModel.RunCommand.CanExecute(null), "Restored original SQLite archive could not restart the same approved batch.");
            }
            await SnapshotAsync(window, Path.Combine(output, "13-batch-approved-started.png"));
            model.ConstructedNormal = false;
            model.ProductId = "SIM-BATCH-PRODUCTION-ONLINE";
            await model.RunCommand.ExecuteAsync(null);
            Require(store.Get(Guid.Parse(model.InspectionId)) is { Purpose: InspectionPurpose.Production, ProductionSequence: 1, Decision: QualityDecision.Fail },
                "Online production did not run real defect detection with sequence 1.");

            fixture.Mode = BatchUiNetwork.ServerError;
            model.ProductId = "SIM-BATCH-PRODUCTION-500";
            await model.RunCommand.ExecuteAsync(null);
            Require(store.Get(Guid.Parse(model.InspectionId)) is { ProductionSequence: 2 } && model.BatchNotice.Contains("中央暂不可达"),
                "A valid started batch did not continue through a 500 response.");
            fixture.Mode = BatchUiNetwork.Disconnected;
            model.ConstructedNormal = true;
            model.ProductId = "SIM-BATCH-PRODUCTION-DISCONNECTED";
            await model.RunCommand.ExecuteAsync(null);
            var disconnected = store.Get(Guid.Parse(model.InspectionId))!;
            Require(disconnected.ProductionSequence == 3 && disconnected.Decision == QualityDecision.Pass && disconnected.ExecutionSessionId == firstSession.Id
                && disconnected.StartedAt < firstSession.ExpiresAt && !model.UseDevelopmentRecipeCommand.CanExecute(null),
                "Disconnected continuation changed batch/session or allowed engineering mode.");
            await SnapshotAsync(window, Path.Combine(output, "14-batch-disconnected.png"));

            fixture.Mode = BatchUiNetwork.Unauthorized;
            model.ProductId = "SIM-BATCH-MUST-NOT-ACCEPT-401";
            await model.RunCommand.ExecuteAsync(null);
            Require(store.ReadRecent().Count == 5 && !model.RunCommand.CanExecute(null) && store.ReadActiveBatch()!.Session is null,
                "An explicit personnel 401 did not stop acceptance and clear the local session.");
            await SnapshotAsync(window, Path.Combine(output, "15-batch-personnel-rejected.png"));
            fixture.Mode = BatchUiNetwork.Online;
            await model.SignOutCommand.ExecuteAsync(null);
            Require(model.OperatorName == "未登录" && store.ReadActiveBatch()!.Session is null, "Sign-out retained the former operator session.");
            var nextPersonnel = StationAuthentication.CreatePersonnelSession(fixture.Address);
            var nextActor = await StationAuthentication.LoginOperatorAsync(nextPersonnel.Client, new("batch-operator-2", BatchUiHttpFixture.Password));
            model.SignIn(nextActor, nextPersonnel, false);
            Require(!model.RunCommand.CanExecute(null) && model.StartBatchCommand.CanExecute(null), "A new operator inherited the prior production authorization.");
            await model.StartBatchCommand.ExecuteAsync(null);
            var nextSession = store.ReadActiveBatch()!.Session!;
            Require(nextSession.Id != firstSession.Id && nextSession.OperatorId == nextActor.Id
                && nextSession.ArchiveId == firstSession.ArchiveId, "Shift start reused the former operator's session or changed its archive.");
            foreach (var sequence in new[] { 4, 5 })
            {
                model.ProductId = $"SIM-BATCH-NEXT-SHIFT-{sequence}";
                await model.RunCommand.ExecuteAsync(null);
                Require(store.Get(Guid.Parse(model.InspectionId)) is { Purpose: InspectionPurpose.Production } record
                    && record.ProductionSequence == sequence && record.OperatorId == nextActor.Id && record.ExecutionSessionId == nextSession.Id,
                    "A new-shift production record changed sequence or operator provenance.");
            }
            Require(!model.RunCommand.CanExecute(null) && model.BatchStateText.Contains("计划数量已接收完毕"), "Quantity limit did not stop the production button.");
            await model.RunCommand.ExecuteAsync(null);
            Require(store.ReadRecent().Count == 7 && store.ReadActiveBatch()!.AcceptedProductionCount == 5, "A disabled quantity gate created another attempt.");
            await AwaitBatchUiAsync(() => model.PendingCount == 0 && fixture.Records.Count == 7, "all immutable fixture receipts");

            var records = store.ReadRecent().Select(row => store.Get(row.Record.Id)!).OrderBy(record => record.StartedAt).ToArray();
            Require(records.Select(record => record.Id).Distinct().Count() == 7 && records.Count(record => record.Purpose == InspectionPurpose.FirstArticle) == 2
                && records.Where(record => record.Purpose == InspectionPurpose.FirstArticle).All(record => record.ProductionSequence is null && record.ExecutionSessionId is null)
                && records.Where(record => record.Purpose == InspectionPurpose.Production).Select(record => record.ProductionSequence).Order().SequenceEqual(new int?[] { 1, 2, 3, 4, 5 }),
                "Purpose or production numbering crossed the first-article boundary.");
            foreach (var record in records)
            {
                Require(record.BatchId == fixture.Batch.Id && record.StationId == fixture.Batch.StationId && record.RecipeId == fixture.Batch.RecipeVersionId.ToString()
                    && record.TestedImage is { Length: > 0 } && record.ReferenceImage!.SequenceEqual(reference)
                    && JsonSerializer.Deserialize<PublishedRecipeVersion>(record.RecipeJson, json)!.BundleHash == fixture.Version.BundleHash
                    && InspectionTransfer.Hash(record) == InspectionTransfer.Hash(fixture.Records[record.Id]), "Immutable local/HTTP fixture archive mismatch.");
                if (record.ProductionSequence is null or <= 3) Require(record.OperatorId == actor.Id, "Shift changed prior record attribution.");
                if (record.SourceKind == "ConstructedNormal") Require(record.TestedImage.SequenceEqual(record.ReferenceImage), "Constructed normal lost its explicit source evidence.");
            }
            model.SelectedHistory = model.History.Single(row => row.Id == records.Last().Id);
            await model.ViewHistoryCommand.ExecuteAsync(null);
            await SnapshotAsync(window, Path.Combine(output, "16-batch-next-shift-complete.png"));
            await File.WriteAllTextAsync(Path.Combine(output, "batch-ui-fixture.json"), JsonSerializer.Serialize(new
            {
                completedAt = DateTimeOffset.UtcNow,
                scope = "Loopback HTTP fixture + actual WPF/Core/OpenCV/SQLite; not a formal quality publication or the SQL end-to-end production chain",
                options.StationId, options.DatabasePath, fixture.Address, batch = fixture.Batch, fixture.Version.BundleHash,
                firstSession, nextSession, firstArticleFailId = fail.Id, firstArticlePassId = pass.Id,
                pending = store.PendingCount(), acceptedProduction = store.ReadActiveBatch()!.AcceptedProductionCount,
                records = records.Select(record => new { record.Id, record.ProductId, record.Purpose, record.ProductionSequence, record.ExecutionSessionId,
                    record.OperatorId, record.RecipeId, record.SourceKind, record.Decision, record.DetectionMs,
                    contentHash = InspectionTransfer.Hash(record), testedSha256 = BatchUiHash(record.TestedImage!), referenceSha256 = BatchUiHash(record.ReferenceImage!) }),
                requests = fixture.Requests.ToArray(),
                checks = new[] { "full batch/cache download", "real first-article Fail retry", "constructed normal Pass waits for receipt and approval",
                    "online per-operator start", "real production", "500 and disconnected same-batch continuation", "explicit 401 stops before Started",
                    "shift clears old session and requires new online start", "quantity stops at five without phantom attempts", "seven immutable archives and image hashes match receipts" }
            }, json));
        }
        finally { window.Close(); }
    }

    private static async Task AwaitBatchUiAsync(Func<bool> condition, string operation)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(35)) await Task.Delay(100);
        Require(condition(), "Timed out waiting for " + operation);
    }

    private static string BatchUiHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private enum BatchUiNetwork { Online, ServerError, Disconnected, Unauthorized }

    // Deliberately isolated protocol fixture. It owns no SQL connection or production publishing route.
    private sealed class BatchUiHttpFixture : IAsyncDisposable
    {
        public const string Password = "Unused!LocalBatchFixture";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly HttpListener listener = new();
        private readonly Task loop;
        private readonly byte[] reference;
        private readonly Dictionary<string, BatchExecutionSession> sessions = [];
        private Guid? archiveId;
        private FirstArticleApproval? approval;
        public volatile bool AllowUploads;
        public volatile BatchUiNetwork Mode;
        public Uri Address { get; }
        public BatchDefinition Batch { get; }
        public PublishedRecipeVersion Version { get; }
        public ConcurrentDictionary<Guid, InspectionRecord> Records { get; } = [];
        public ConcurrentQueue<object> Requests { get; } = [];
        public Guid? BoundArchiveId => archiveId;

        public BatchUiHttpFixture(string sampleId, byte[] reference)
        {
            this.reference = reference;
            using var available = new TcpListener(IPAddress.Loopback, 0);
            available.Start();
            var port = ((IPEndPoint)available.LocalEndpoint).Port;
            available.Stop();
            Address = new Uri($"http://127.0.0.1:{port}/");
            var targets = new RecipeTargets(0.99, 0.99, 100);
            var bundle = new PublishedRecipeBundle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "隔离批次 UI 夹具 · 非质量发布", "Classical",
                new RecipeClassicalSettings(BoxPadding: 4), targets, targets, new(640, 640, true),
                BatchUiHash(File.ReadAllBytes(typeof(ClassicalDetector).Assembly.Location)), new string('3', 64), new string('4', 64),
                [new(sampleId, Guid.NewGuid(), BatchUiHash(reference), reference.Length)], "fixture-engineer", "隔离 UI 夹具", DateTimeOffset.UtcNow);
            Version = new(bundle, PublishedRecipeTransfer.Hash(bundle));
            Batch = new(Guid.NewGuid(), "SIM-UI-BATCH", "PCB", "TOP", 5, "BATCH-UI-STATION", bundle.VersionId, Version.BundleHash,
                "fixture-engineer", "隔离 UI 夹具", DateTimeOffset.UtcNow);
            listener.Prefixes.Add(Address.ToString());
            listener.Start();
            loop = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    try { await RespondAsync(context); }
                    catch (Exception error)
                    {
                        Requests.Enqueue(new { error = error.Message });
                        try { context.Response.StatusCode = 500; } catch (ObjectDisposedException) { }
                    }
                    finally { context.Response.Close(); }
                }
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var path = request.Url!.AbsolutePath;
            Requests.Enqueue(new { at = DateTimeOffset.UtcNow, method = request.HttpMethod, path, mode = Mode.ToString() });
            var userName = request.Cookies["batch-fixture-user"]?.Value;
            var user = User(userName);
            if (path == "/api/auth/login")
            {
                var login = await JsonSerializer.DeserializeAsync<LoginRequest>(request.InputStream, Json);
                var identity = User(login?.UserName);
                if (login?.Password != Password || identity is null) { context.Response.StatusCode = 401; return; }
                context.Response.SetCookie(new Cookie("batch-fixture-user", identity.UserName, "/"));
                await Reply(context, identity); return;
            }
            if (path == "/api/auth/logout")
            {
                context.Response.SetCookie(new Cookie("batch-fixture-user", "", "/") { Expires = DateTime.Now.AddDays(-1) });
                context.Response.StatusCode = 204; return;
            }
            if (path == "/api/auth/me")
            {
                if (Mode == BatchUiNetwork.Disconnected) { context.Response.Abort(); return; }
                if (Mode != BatchUiNetwork.Online) { context.Response.StatusCode = Mode == BatchUiNetwork.Unauthorized ? 401 : 500; return; }
                if (user is null) { context.Response.StatusCode = 401; return; }
                await Reply(context, user); return;
            }
            if (path.StartsWith("/api/inspections/") && request.HttpMethod == "PUT")
            {
                if (user?.StationId != Batch.StationId) { context.Response.StatusCode = 401; return; }
                if (!AllowUploads || Mode != BatchUiNetwork.Online) { context.Response.StatusCode = 503; return; }
                var record = (await JsonSerializer.DeserializeAsync<InspectionRecord>(request.InputStream, Json))!;
                Require(record.StationId == Batch.StationId && record.BatchId == Batch.Id && record.RecipeId == Batch.RecipeVersionId.ToString(), "Unexpected fixture archive identity.");
                var created = Records.TryAdd(record.Id, record);
                if (!created && InspectionTransfer.Hash(Records[record.Id]) != InspectionTransfer.Hash(record)) { context.Response.StatusCode = 409; return; }
                await Reply(context, new InspectionReceipt(record.Id, InspectionTransfer.Hash(record), record.CompletedAt!.Value.AddMilliseconds(1)), created ? 201 : 200); return;
            }
            if (path.EndsWith("/first-article-approval") && user?.Roles.Contains("QualityEngineer") == true)
            {
                var input = (await JsonSerializer.DeserializeAsync<ApproveFirstArticleRequest>(request.InputStream, Json))!;
                if (!Records.TryGetValue(input.InspectionId, out var record) || record.Purpose != InspectionPurpose.FirstArticle || record.Decision != QualityDecision.Pass
                    || record.ExecutionStatus != InspectionExecution.Completed || record.TestedImage is null || record.ReferenceImage is null) { context.Response.StatusCode = 409; return; }
                approval = new(Batch.Id, record.Id, user.Id, user.DisplayName, DateTimeOffset.UtcNow);
                await Reply(context, approval, 201); return;
            }
            if (path.EndsWith("/execution-sessions") && user?.Roles.Contains("Operator") == true)
            {
                var input = (await JsonSerializer.DeserializeAsync<StartBatchRequest>(request.InputStream, Json))!;
                if (approval is null || input.StationId != Batch.StationId || input.RecipeBundleHash != Version.BundleHash) { context.Response.StatusCode = 409; return; }
                if (input.ArchiveId == Guid.Empty) { context.Response.StatusCode = 400; return; }
                if (archiveId is not null && archiveId != input.ArchiveId) { context.Response.StatusCode = 409; return; }
                archiveId ??= input.ArchiveId;
                if (!sessions.TryGetValue(user.Id, out var session))
                {
                    session = new(Guid.NewGuid(), Batch.Id, Batch.StationId, Version.BundleHash, approval.InspectionId,
                        user.Id, user.DisplayName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), input.ArchiveId);
                    sessions.Add(user.Id, session);
                }
                await Reply(context, session, 201); return;
            }
            if (user?.StationId != Batch.StationId) { context.Response.StatusCode = 403; return; }
            var status = approval is null ? BatchStatus.AwaitingFirstArticle : sessions.Count == 0 ? BatchStatus.Approved : BatchStatus.InProgress;
            if (path == "/api/station/batches") { await Reply(context, new[] { new BatchSummary(Batch, status, Records.Values.Count(record => record.Purpose == InspectionPurpose.Production)) }); return; }
            if (path == $"/api/station/batches/{Batch.Id}/package") { await Reply(context, new BatchPackage(Batch, status, approval, Version)); return; }
            if (path == $"/api/recipes/versions/{Batch.RecipeVersionId}/bundle") { await Reply(context, Version); return; }
            if (path == $"/api/recipe-assets/{Version.Bundle.References[0].AssetId}")
            { context.Response.ContentType = "image/jpeg"; context.Response.ContentLength64 = reference.Length; await context.Response.OutputStream.WriteAsync(reference); return; }
            context.Response.StatusCode = 404;
        }

        private CurrentUser? User(string? name) => name switch
        {
            "batch-device" => new(name, name, "隔离设备夹具", ["Station"], Batch.StationId),
            "batch-operator-1" => new(name, name, "首班操作员", ["Operator"], null),
            "batch-operator-2" => new(name, name, "次班操作员", ["Operator"], null),
            "batch-quality" => new(name, name, "夹具质量人员", ["QualityEngineer"], null),
            _ => null
        };

        private static async Task Reply<T>(HttpListenerContext context, T value, int status = 200)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
        }

        public async ValueTask DisposeAsync() { listener.Stop(); listener.Close(); await loop; }
    }
}
