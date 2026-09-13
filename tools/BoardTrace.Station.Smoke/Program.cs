using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Smoke;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var server = args.Length == 0 ? null : args.Length == 2 && args[0] == "--server"
            ? new Uri(args[1].TrimEnd('/') + "/", UriKind.Absolute)
            : throw new ArgumentException("Supported option: --server <HTTP URL>.");
        var output = Path.GetFullPath($"artifacts/station/{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(output);
        using var bindingLog = new TextWriterTraceListener(Path.Combine(output, "binding-errors.log"));
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingLog);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.InitializeComponent();
        var exitCode = 1;
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                await RunAsync(output, server);
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
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task RunAsync(string output, Uri? server)
    {
        var options = new StationOptions("T05-SMOKE", Path.GetFullPath("data"),
            Path.GetFullPath("training/manifests/inputs/validation.jsonl"), Path.Combine(output, "station.db"), new Uri("http://127.0.0.1:1/"));
        await using var model = new StationViewModel(options);
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
        while (model.UploadStatus != "中央暂不可达" && timeout.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(100);
        Require(model.UploadStatus == "中央暂不可达" && model.PendingCount == 2 && model.RunCommand.CanExecute(null),
            "An unavailable central server did not preserve pending records and engineering replay availability.");
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
        if (server != null) await VerifyUploadedHistoryAsync(output, options with { ServerUrl = server });
    }

    private static async Task VerifyUploadedHistoryAsync(string output, StationOptions defaults)
    {
        var options = defaults with { StationId = "T06-UI-" + Guid.NewGuid().ToString("N")[..8], DatabasePath = Path.Combine(output, "central-sync.db") };
        await using var model = new StationViewModel(options);
        var window = new MainWindow { DataContext = model };
        window.Show();
        await model.InitializeAsync();
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
            pendingUploads = store.PendingCount(),
            checks = new[] { "real central acknowledgments", "upload and inspection refresh overlap", "latest three records retained", "selected history retained", "original defect image and overlay retained" }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        window.Close();
    }

    private static async Task VerifyCloseWhileInspectingAsync(string output, StationOptions defaults)
    {
        var options = defaults with { DatabasePath = Path.Combine(output, "closing-inspection.db") };
        await using var model = new StationViewModel(options);
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
        await using var model = new StationViewModel(defaults with { DatabasePath = Path.Combine(output, "closing-initialization.db") });
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
        await using var stoppedModel = new StationViewModel(unopened);
        await stoppedModel.DisposeAsync();
        await stoppedModel.InitializeAsync();
        Require(!File.Exists(unopened.DatabasePath) && !stoppedModel.CanEdit,
            "Initialization after close created new resources or enabled commands.");
    }

    private static async Task VerifyCloseWhileReadingAsync(string output, StationOptions defaults)
    {
        await using var model = new StationViewModel(defaults with { DatabasePath = Path.Combine(output, "closing-inspection.db") });
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
}
