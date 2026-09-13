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
    public static int Main()
    {
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
                await RunAsync(output);
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

    private static async Task RunAsync(string output)
    {
        var options = new StationOptions("T05-SMOKE", Path.GetFullPath("data"),
            Path.GetFullPath("training/manifests/inputs/validation.jsonl"), Path.Combine(output, "station.db"));
        var model = new StationViewModel(options);
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
            checks = new[] { "actual WPF window", "defect replay", "controlled normal", "historical images and result", "storage rollback", "fault stops input", "no stale history selection" }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        window.Close();
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
