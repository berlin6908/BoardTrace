using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

namespace BoardTrace.Station.Tests;

public sealed class InspectionCoordinatorTests
{
    private static (LocalInspectionStore Store, ReplayImageSource Source) Setup(bool corrupt = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        using var reference = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
        Cv2.Rectangle(reference, new Rect(83, 65, 300, 35), Scalar.Black, -1);
        Cv2.Rectangle(reference, new Rect(348, 65, 35, 440), Scalar.Black, -1);
        Cv2.Circle(reference, new Point(200, 450), 60, Scalar.Black, -1);
        File.WriteAllBytes(Path.Combine(folder, "reference.png"), reference.ToBytes(".png"));
        File.WriteAllBytes(Path.Combine(folder, "tested.png"), corrupt ? [1, 2, 3] : reference.ToBytes(".png"));
        var store = new LocalInspectionStore(Path.Combine(folder, "station.db"));
        store.Initialize();
        return (store, new ReplayImageSource(folder, new ReplaySample("controlled-1", "tested.png", "reference.png")));
    }

    [Fact]
    public async Task RealImageDetectionIsReturnedOnlyWithItsDurableArchive()
    {
        var (store, source) = Setup();
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        var result = await coordinator.InspectAsync("TEST-01", "SIM-001", source);
        var archived = store.Get(result.Id)!;
        Assert.Equal(QualityDecision.Pass, result.Decision);
        Assert.Equal(InspectionExecution.Completed, archived.ExecutionStatus);
        Assert.Equal(result.TestedImage, archived.TestedImage);
        Assert.NotEmpty(archived.TestedImage!);
        Assert.Equal(1, store.PendingCount());
    }

    [Fact]
    public async Task CorruptImageIsArchivedAsFailedWithoutAQualityDecision()
    {
        var (store, source) = Setup(corrupt: true);
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        var result = await coordinator.InspectAsync("TEST-01", "SIM-002", source);
        Assert.Equal(InspectionExecution.Failed, result.ExecutionStatus);
        Assert.Equal(QualityDecision.NotEvaluated, result.Decision);
        Assert.Empty(result.Defects);
        Assert.NotNull(result.Error);
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Get(result.Id)!.TestedImage);
        Assert.Equal(1, store.PendingCount());
        Assert.False(coordinator.IsFaulted);
    }

    [Fact]
    public async Task CommitFailureStopsTheStationAndNeverReturnsAValidResult()
    {
        var (store, source) = Setup();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_outbox BEFORE INSERT ON UploadState BEGIN SELECT RAISE(ABORT, 'simulated full disk'); END;";
            command.ExecuteNonQuery();
        }
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InspectAsync("TEST-01", "SIM-003", source));
        Assert.True(coordinator.IsFaulted);
        var stored = Assert.Single(store.ReadRecent()).Record;
        Assert.Equal(QualityDecision.NotEvaluated, stored.Decision);
        Assert.Null(store.Get(stored.Id)!.TestedImage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InspectAsync("TEST-01", "SIM-004", source));
        Assert.Single(store.ReadRecent());
    }

    [Fact]
    public async Task BusyStationDoesNotQueueOrStartAnotherInspection()
    {
        var (store, source) = Setup();
        var delayed = new PausedSource(source);
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        var first = coordinator.InspectAsync("TEST-01", "SIM-005", delayed);
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InspectAsync("TEST-01", "SIM-006", source));
            Assert.Single(store.ReadRecent());
        }
        finally { delayed.Release.SetResult(); }
        await first;
        Assert.Single(store.ReadRecent());
    }

    private sealed class PausedSource(IImageSource source) : IImageSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SampleId => source.SampleId;
        public string SourceKind => source.SourceKind;
        public async Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await source.CaptureAsync(cancellationToken);
        }
    }
}
