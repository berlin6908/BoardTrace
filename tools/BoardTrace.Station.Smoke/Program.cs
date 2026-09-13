using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Smoke;

public static partial class Program
{
    private static readonly CurrentUser OfflineOperator = new("smoke-operator", "smoke-operator", "测试操作员", ["Operator"], null);

    [STAThread]
    public static int Main(string[] args)
    {
        var arguments = new Dictionary<string, string>();
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--server" or "--station" or "--credentials" or "--development-accounts"))
                throw new ArgumentException("Supported options: --server, --station, --credentials, --development-accounts.");
            arguments.Add(args[index], args[index + 1]);
        }
        var output = Path.GetFullPath($"artifacts/station/{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(output);
        using var bindingLog = new TextWriterTraceListener(Path.Combine(output, "binding-errors.log"));
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        // A production App schedules its own OnStartup login as soon as the dispatcher runs.
        // Use a plain Application while loading the same visual resources for screenshots.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var appMarkup = XDocument.Load(Path.GetFullPath("src/BoardTrace.Station/App.xaml"));
        var resourceMarkup = appMarkup.Root!.Elements().Single(element => element.Name.LocalName == "Application.Resources");
        var dictionaryMarkup = new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"), resourceMarkup.Elements());
        application.Resources = (ResourceDictionary)XamlReader.Parse(dictionaryMarkup.ToString(SaveOptions.DisableFormatting));
        var exitCode = 1;
        _ = application.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await RunAsync(output, arguments);
                bindingLog.Flush();
                if (new FileInfo(Path.Combine(output, "binding-errors.log")).Length != 0)
                    throw new InvalidOperationException("WPF binding errors were recorded.");
                Console.WriteLine($"WPF smoke passed: {output}");
                exitCode = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
            }
            finally
            {
                application.Shutdown();
            }
        });
        application.Run();
        return exitCode;
    }

    private static async Task RunAsync(string output, Dictionary<string, string> arguments)
    {
        using (var previewClient = StationAuthentication.CreateClient(new Uri("http://127.0.0.1:1/")))
        {
            var preview = new LoginWindow(previewClient, "STATION-01");
            preview.Show();
            await SnapshotAsync(preview, Path.Combine(output, "00-operator-login.png"));
            preview.Close();
        }
        var options = new StationOptions("T05-SMOKE", Path.GetFullPath("data"),
            Path.GetFullPath("training/manifests/inputs/validation.jsonl"), Path.Combine(output, "station.db"), new Uri("http://127.0.0.1:1/"), Path.Combine(output, "offline-device.json"));
        File.WriteAllText(options.CredentialsPath, JsonSerializer.Serialize(new StationCredentials("offline-test-device", "unused-test-password")));
        await using var model = OfflineModel(options);
        var window = new MainWindow { DataContext = model };
        window.Show();
        await model.InitializeAsync();
        Require(model.RunCommand.CanExecute(null), model.Notice);
        await SnapshotAsync(window, Path.Combine(output, "01-ready.png"));

        model.ProductId = "SIM-UI-DEFECT";
        await model.RunCommand.ExecuteAsync(null);
        Require(model.Decision == "缺陷" && model.PendingCount == 1, "Defect replay did not produce a saved defect result.");
        var defectId = Guid.Parse(model.InspectionId);
        await SnapshotAsync(window, Path.Combine(output, "02-defect.png"));

        model.SelectedHistory = model.History.Single();
        model.ConstructedNormal = true;
        model.ProductId = "SIM-UI-CONTROLLED-NORMAL";
        await model.RunCommand.ExecuteAsync(null);
        Require(model.Decision == "合格" && model.PendingCount == 2, "Controlled normal did not produce a saved pass.");
        Require(model.SelectedHistory is null, "An old history selection remained attached to the new result.");
        var normalId = Guid.Parse(model.InspectionId);
        var timeout = Stopwatch.StartNew();
        // Allow more than one 5-second background interval plus the 10-second HTTP timeout.
        while (model.UploadStatus != "中央暂不可达" && timeout.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(100);
        File.WriteAllText(Path.Combine(output, "offline-upload-observation.json"), JsonSerializer.Serialize(new
        {
            observedAt = DateTimeOffset.UtcNow, waitedSeconds = timeout.Elapsed.TotalSeconds,
            model.UploadStatus, model.UploadNotice, model.PendingCount, canRun = model.RunCommand.CanExecute(null)
        }));
        Require(model.UploadStatus == "中央暂不可达" && model.PendingCount == 2 && model.RunCommand.CanExecute(null),
            $"Unavailable central observation: {model.UploadStatus}; pending={model.PendingCount}; canRun={model.RunCommand.CanExecute(null)}; {model.UploadNotice}");
        await SnapshotAsync(window, Path.Combine(output, "03-controlled-normal.png"));

        model.SelectedHistory = model.History.Single(row => row.Id == defectId);
        await model.ViewHistoryCommand.ExecuteAsync(null);
        Require(model.InspectionId == defectId.ToString() && model.Decision == "缺陷" && model.DefectOverlays.Count > 0,
            "Reading a historical record did not restore its original decision and overlays.");

        using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_outbox BEFORE INSERT ON UploadState BEGIN SELECT RAISE(ABORT, 'smoke: simulated storage failure'); END;";
            command.ExecuteNonQuery();
        }
        model.ProductId = "SIM-UI-WRITE-FAILURE";
        await model.RunCommand.ExecuteAsync(null);
        Require(model.Decision == "未判定" && model.TestedImage is null && model.ReferenceImage is null,
            "The screen exposed a valid result after a failed transaction.");
        Require(!model.RunCommand.CanExecute(null), "The UI still accepts inspections after a storage fault.");
        await SnapshotAsync(window, Path.Combine(output, "04-storage-fault.png"));

        var store = new LocalInspectionStore(options.DatabasePath);
        var defect = store.Get(defectId)!;
        var normal = store.Get(normalId)!;
        Require(defect.TestedImage!.Length > 0 && defect.ReferenceImage!.Length > 0 && store.PendingCount() == 2,
            "SQLite did not retain the two complete image/result/outbox records.");
        Require(normal.SourceKind == "ConstructedNormal" && normal.TestedImage!.SequenceEqual(normal.ReferenceImage!),
            "Controlled normal input was not identified and archived correctly.");
        var interrupted = store.ReadRecent().Single(row => row.Record.ProductId == "SIM-UI-WRITE-FAILURE");
        var unfinished = store.Get(interrupted.Record.Id)!;
        Require(unfinished.Decision == QualityDecision.NotEvaluated && unfinished.TestedImage is null && !interrupted.PendingUpload,
            "A failed transaction left a result or upload record behind.");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            completedAt = DateTimeOffset.UtcNow,
            stationAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location))),
            database = options.DatabasePath, defectId, normalId,
            defectRegions = defect.Defects.Count, defectDetectionMs = defect.DetectionMs,
            normalDetectionMs = normal.DetectionMs, pendingUploads = store.PendingCount(),
            totalAttempts = store.ReadRecent().Count,
            checks = new[] { "actual WPF window", "defect replay", "controlled normal", "historical images and result", "storage rollback", "fault stops input", "no stale history selection", "central unavailable preserves pending and engineering replay" }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        window.Close();
        await VerifyCloseWhileInspectingAsync(output, options);
        await VerifyCloseWhileReadingAsync(output, options);
        await VerifyCloseDuringInitializationAsync(output, options);
        await VerifySessionAndShiftAsync(output, options);
        await VerifyPublishedRecipeAsync(output, options);
        if (arguments.TryGetValue("--server", out var server))
        {
            var station = arguments.GetValueOrDefault("--station", "STATION-01");
            await VerifyUploadedHistoryAsync(output, options with
            {
                ServerUrl = new Uri(server.TrimEnd('/') + "/"), StationId = station,
                CredentialsPath = Path.GetFullPath(arguments.GetValueOrDefault("--credentials", $".local/stations/{station}.json"))
            }, arguments.GetValueOrDefault("--development-accounts", ".local/development-accounts.json"));
        }
    }

    private static async Task VerifyUploadedHistoryAsync(string output, StationOptions defaults, string developmentAccounts)
    {
        var options = defaults with { DatabasePath = Path.Combine(output, "central-sync.db") };
        using var operatorClient = StationAuthentication.CreateClient(options.ServerUrl);
        var passwords = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(developmentAccounts))
            ?? throw new InvalidDataException("No development operator login was found.");
        var request = new LoginRequest("operator", passwords["operator"]);
        var login = new LoginWindow(operatorClient, options.StationId);
        var loginTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        loginTimeout.Tick += (_, _) => login.Close();
        _ = login.Dispatcher.InvokeAsync(async () =>
        {
            ((TextBox)login.FindName("UserNameInput")).Text = request.UserName;
            await SnapshotAsync(login, Path.Combine(output, "00-operator-login.png"));
            ((PasswordBox)login.FindName("PasswordInput")).Password = request.Password;
            ((Button)login.FindName("LoginButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }, DispatcherPriority.Loaded);
        bool? loggedIn;
        try { loginTimeout.Start(); loggedIn = login.ShowDialog(); }
        finally { loginTimeout.Stop(); }
        Require(loggedIn == true && login.AuthenticatedUser != null && ((PasswordBox)login.FindName("PasswordInput")).Password.Length == 0,
            "Actual operator login failed or PasswordBox was not cleared.");
        var user = login.AuthenticatedUser!;
        await using var model = new StationViewModel(options, user, operatorClient);
        var window = new MainWindow { DataContext = model };
        window.Show();
        await model.InitializeAsync();
        await model.RefreshRecipesCommand.ExecuteAsync(null);
        var publications = await operatorClient.GetFromJsonAsync<PublishedRecipeSummary[]>("api/recipes/versions") ?? [];
        Require(model.PublishedRecipes.Select(choice => choice.Summary.Id).Order().SequenceEqual(publications.Select(version => version.Id).Order())
            && (publications.Length != 0 || model.RecipeNotice.Contains("中央尚无已发布方案")),
            "The station's published version list differs from the real central service.");
        model.ProductId = "SIM-UPLOAD-DEFECT";
        await model.RunCommand.ExecuteAsync(null);
        var firstId = Guid.Parse(model.InspectionId);
        model.ProductId = "SIM-UPLOAD-CONTROLLED-NORMAL";
        model.ConstructedNormal = true;
        await model.RunCommand.ExecuteAsync(null);
        var nextInspection = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += OnUploadChanged;
        void OnUploadChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
        {
            if (change.PropertyName != nameof(model.UploadStatus) || model.UploadStatus != "最近同步成功" || nextInspection.Task.IsCompleted) return;
            model.ProductId = "SIM-UPLOAD-CONCURRENT-INSPECTION";
            nextInspection.SetResult(model.RunCommand.ExecuteAsync(null));
        }
        try
        {
            var accepted = await nextInspection.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await accepted;
        }
        finally { model.PropertyChanged -= OnUploadChanged; }
        model.SelectedHistory = model.History.Single(row => row.Id == firstId);
        var timeout = Stopwatch.StartNew();
        while ((model.PendingCount != 0 || model.History.Count != 3 || model.History.Any(row => row.Stored.PendingUpload)) &&
               timeout.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(100);
        var store = new LocalInspectionStore(options.DatabasePath);
        var rows = store.ReadRecent();
        Require(rows.Count == 3 && rows.All(row => row.AcknowledgedAt != null) && store.PendingCount() == 0,
            "Three actual inspections were not confirmed by the central service.");
        Require(rows.All(row => row.Record.OperatorId == user.Id && row.Record.OperatorName == user.DisplayName),
            "Archived operator attribution does not match the authenticated person.");
        var centralChecks = new List<object>();
        foreach (var row in rows)
        {
            var original = store.Get(row.Record.Id)!;
            using var detail = await operatorClient.GetFromJsonAsync<JsonDocument>($"api/inspections/{original.Id}")
                ?? throw new InvalidDataException("Central detail response was empty.");
            var central = detail.RootElement.GetProperty("inspection").Deserialize<InspectionRecord>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var tested = await operatorClient.GetByteArrayAsync($"api/inspections/{original.Id}/images/tested");
            var reference = await operatorClient.GetByteArrayAsync($"api/inspections/{original.Id}/images/reference");
            var contentHash = detail.RootElement.GetProperty("contentHash").GetString();
            Require(contentHash == InspectionTransfer.Hash(original) && tested.SequenceEqual(original.TestedImage!) &&
                    reference.SequenceEqual(original.ReferenceImage!) &&
                    InspectionTransfer.Hash(central with { TestedImage = tested, ReferenceImage = reference }) == contentHash,
                "Personnel API read did not match the local immutable document, operator attribution or image bytes.");
            centralChecks.Add(new { original.Id, original.OperatorId, contentHash, testedBytes = tested.Length, referenceBytes = reference.Length });
        }
        Require(model.History.Select(row => row.Id).SequenceEqual(rows.Select(row => row.Record.Id)) && model.PendingCount == 0 &&
                model.History.All(row => !row.Stored.PendingUpload),
            "Overlapping upload/detection refreshes replaced current history with a stale snapshot.");
        Require(model.SelectedHistory?.Id == firstId, "An automatic history refresh lost the user's selected record.");
        await model.ViewHistoryCommand.ExecuteAsync(null);
        Require(model.Decision == "缺陷" && model.DefectOverlays.Count > 0, "The selected uploaded record did not retain its original defect evidence.");
        await SnapshotAsync(window, Path.Combine(output, "05-central-synced.png"));
        File.WriteAllText(Path.Combine(output, "central-sync.json"), JsonSerializer.Serialize(new
        {
            completedAt = DateTimeOffset.UtcNow, options.StationId, server = options.ServerUrl,
            inspectionIds = rows.Select(row => row.Record.Id), selectedInspection = model.SelectedHistory?.Id,
            pendingUploads = store.PendingCount(), operatorId = user.Id, operatorName = user.DisplayName, centralChecks,
            checks = new[] { "real PasswordBox login", "password cleared", "authenticated operator attribution", "real device login and central acknowledgments", "personnel API reads match full documents and image bytes", "upload and inspection refresh overlap", "latest three records retained", "selected history retained", "original defect image and overlay retained" }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        model.ProductId = "SIM-UPLOAD-AFTER-PERSONNEL-LOGOUT";
        await model.RunCommand.ExecuteAsync(null);
        var afterLogoutId = Guid.Parse(model.InspectionId);
        await model.SignOutCommand.ExecuteAsync(null);
        timeout.Restart();
        while (model.PendingCount != 0 && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
        var afterLogout = store.Get(afterLogoutId)!;
        Require(model.OperatorName == "未登录" && !model.RunCommand.CanExecute(null) && store.PendingCount() == 0 &&
                store.ReadRecent().Single(row => row.Record.Id == afterLogoutId).AcknowledgedAt != null,
            "Device upload did not finish independently after personnel sign-out.");
        Require(afterLogout.OperatorId == user.Id && afterLogout.OperatorName == user.DisplayName,
            "Personnel sign-out changed the archived operator attribution.");
        await SnapshotAsync(window, Path.Combine(output, "06-signedout-synced.png"));
        File.WriteAllText(Path.Combine(output, "signedout-upload.json"), JsonSerializer.Serialize(new
        {
            completedAt = DateTimeOffset.UtcNow, afterLogoutId, afterLogout.OperatorId, afterLogout.OperatorName,
            pendingUploads = store.PendingCount(),
            checks = new[] { "personnel cookie signed out", "new inspections disabled", "device upload continues without personnel session", "original attribution retained" }
        }));
        window.Close();
    }

    private static async Task VerifySessionAndShiftAsync(string output, StationOptions defaults)
    {
        var options = defaults with { DatabasePath = Path.Combine(output, "session-shift.db") };
        var expired = new OperatorHandler(OfflineOperator) { MeStatus = HttpStatusCode.Unauthorized };
        await using var model = new StationViewModel(options, OfflineOperator, PersonnelClient(expired));
        var window = new MainWindow { DataContext = model };
        window.Show();
        await model.InitializeAsync();
        var store = new LocalInspectionStore(options.DatabasePath);
        await model.RunCommand.ExecuteAsync(null);
        Require(store.ReadRecent().Count == 0 && !model.RunCommand.CanExecute(null) && model.SignOutCommand.CanExecute(null),
            "Expired personnel login created an attempt or still accepts new inspections.");
        await model.SignOutCommand.ExecuteAsync(null);
        Require(!model.CanEdit, "An unauthenticated station still accepts commands.");
        await SnapshotAsync(window, Path.Combine(output, "07-personnel-signedout.png"));

        var nextOperator = OfflineOperator with { Id = "smoke-operator-two", DisplayName = "第二位测试操作员" };
        var nextHandler = new OperatorHandler(nextOperator) { Disconnected = true };
        model.SignIn(nextOperator, PersonnelClient(nextHandler));
        await model.RunCommand.ExecuteAsync(null);
        Require(store.ReadRecent().Count == 0, "Personnel authentication network failure left a Started record behind.");
        nextHandler.Disconnected = false;
        model.ConstructedNormal = true;
        model.ProductId = "SIM-BEFORE-SHIFT";
        await model.RunCommand.ExecuteAsync(null);
        var beforeShiftId = Guid.Parse(model.InspectionId);
        using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
        {
            connection.Open();
            using var writerLock = connection.BeginTransaction();
            model.ProductId = "SIM-SHIFT-WHILE-ACCEPTED";
            var inspection = model.RunCommand.ExecuteAsync(null);
            var acceptedTimeout = Stopwatch.StartNew();
            while (model.Notice != "正在处理，完成本地保存后显示判定。" && !inspection.IsCompleted && acceptedTimeout.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10);
            var signingOut = model.SignOutCommand.ExecuteAsync(null);
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Require(!signingOut.IsCompleted && !model.CanEdit, "Personnel sign-out interrupted an accepted inspection.");
            }
            finally { writerLock.Rollback(); }
            await signingOut;
            Require(inspection.IsCompletedSuccessfully && store.ReadRecent().Count == 2, "Shift change lost an accepted inspection.");
        }
        model.SignIn(OfflineOperator, OfflineOperatorClient(OfflineOperator));
        model.ProductId = "SIM-AFTER-SHIFT";
        await model.RunCommand.ExecuteAsync(null);
        Require(store.Get(beforeShiftId)!.OperatorId == nextOperator.Id && store.Get(Guid.Parse(model.InspectionId))!.OperatorId == OfflineOperator.Id,
            "New personnel identity rewrote an earlier inspection attribution.");
        File.WriteAllText(Path.Combine(output, "session-shift.json"), JsonSerializer.Serialize(new
        {
            completedAt = DateTimeOffset.UtcNow, attempts = store.ReadRecent().Count,
            checks = new[] { "401 before acceptance leaves no record", "network failure before acceptance leaves no record", "sign-out disables new commands", "shift waits for accepted inspection commit", "new operator cannot overwrite previous attribution" }
        }));
        window.Close();
    }

    private static async Task VerifyCloseWhileInspectingAsync(string output, StationOptions defaults)
    {
        var options = defaults with { DatabasePath = Path.Combine(output, "closing-inspection.db") };
        await using var model = OfflineModel(options);
        var window = new MainWindow { DataContext = model };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await model.InitializeAsync();
        using var connection = new SqliteConnection($"Data Source={options.DatabasePath}");
        connection.Open();
        using var writerLock = connection.BeginTransaction();
        model.ConstructedNormal = true;
        model.ProductId = "SIM-CLOSE-DURING-DETECTION";
        var inspection = model.RunCommand.ExecuteAsync(null);
        try
        {
            window.Close();
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Require(!closed.Task.IsCompleted, "Window closed while an accepted inspection was blocked before local commit.");
            Require(!model.CanEdit && !model.RunCommand.CanExecute(null) && !model.ViewHistoryCommand.CanExecute(null),
                "Closing did not disable new commands.");
            Require(!model.DisposeAsync().IsCompleted, "A repeated close returned before the accepted inspection completed.");
        }
        finally { writerLock.Rollback(); }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Require(inspection.IsCompletedSuccessfully, "Window closed before the accepted inspection completed.");
        var store = new LocalInspectionStore(options.DatabasePath);
        var row = store.ReadRecent().Single();
        var record = store.Get(row.Record.Id)!;
        Require(record.ExecutionStatus == InspectionExecution.Completed && record.Decision == QualityDecision.Pass &&
                record.TestedImage is { Length: > 0 } && record.ReferenceImage is { Length: > 0 } && row.PendingUpload,
            "Normal close interrupted the accepted inspection or lost its durable image/result/outbox transaction.");
        File.WriteAllText(Path.Combine(output, "closing-inspection.json"), JsonSerializer.Serialize(new
        {
            completedAt = DateTimeOffset.UtcNow, inspectionId = record.Id,
            record.ExecutionStatus, record.Decision, row.PendingUpload,
            checks = new[] { "close waits for accepted inspection", "repeated close still waits", "new commands disabled", "complete images/result/outbox before exit" }
        }));
    }

    private static async Task VerifyCloseDuringInitializationAsync(string output, StationOptions defaults)
    {
        await using var model = OfflineModel(defaults with { DatabasePath = Path.Combine(output, "closing-initialization.db") });
        var window = new MainWindow { DataContext = model };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        var initialization = model.InitializeAsync();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Require(initialization.IsCompletedSuccessfully && !model.CanEdit && !model.RunCommand.CanExecute(null),
            "Window closed before initialization ended or initialization re-enabled a closing station.");
        File.WriteAllText(Path.Combine(output, "closing-initialization.json"), JsonSerializer.Serialize(new
        {
            completedAt = DateTimeOffset.UtcNow,
            checks = new[] { "close awaits initialization", "initialization cannot re-enable a closing station" }
        }));

        var unopened = defaults with { DatabasePath = Path.Combine(output, "never-started.db") };
        await using var stoppedModel = OfflineModel(unopened);
        await stoppedModel.DisposeAsync();
        await stoppedModel.InitializeAsync();
        Require(!File.Exists(unopened.DatabasePath) && !stoppedModel.CanEdit,
            "Initialization after close created new resources or enabled commands.");
    }

    private static async Task VerifyCloseWhileReadingAsync(string output, StationOptions defaults)
    {
        await using var model = OfflineModel(defaults with { DatabasePath = Path.Combine(output, "closing-inspection.db") });
        var window = new MainWindow { DataContext = model };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await model.InitializeAsync();
        model.SelectedHistory = model.History.Single();
        var reading = model.ViewHistoryCommand.ExecuteAsync(null);
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Require(reading.IsCompletedSuccessfully && model.TestedImage != null && model.ReferenceImage != null && model.Decision == "合格",
            "Window closed before its accepted historical image read finished.");
        File.WriteAllText(Path.Combine(output, "closing-history.json"), JsonSerializer.Serialize(new
        {
            completedAt = DateTimeOffset.UtcNow,
            checks = new[] { "close awaits accepted history read", "historical images decoded before exit" }
        }));
    }

    private static async Task SnapshotAsync(Window window, string path)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static StationViewModel OfflineModel(StationOptions options) => new(options, OfflineOperator, OfflineOperatorClient(OfflineOperator));

    private static HttpClient OfflineOperatorClient(CurrentUser user) => PersonnelClient(new OperatorHandler(user));
    private static HttpClient PersonnelClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://offline-personnel-test/") };

    // Explicit personnel fixture for local image/storage rendering tests. Actual App and live smoke use the login endpoint.
    private sealed class OperatorHandler(CurrentUser user) : HttpMessageHandler
    {
        public HttpStatusCode MeStatus { get; set; } = HttpStatusCode.OK;
        public bool Disconnected { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Disconnected) throw new HttpRequestException("Simulated personnel connection loss.");
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/me" => new HttpResponseMessage(MeStatus) { Content = JsonContent.Create(user) },
                "/api/auth/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException("The offline personnel fixture only supports me/logout.")
            });
        }
    }
}
