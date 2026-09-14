using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

namespace BoardTrace.Station.Tests;

public sealed class InspectionCoordinatorTests
{
    private static readonly CurrentUser Operator = new("operator-1", "operator1", "Operator One", ["Operator"], null);
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
    public async Task SwitchingOperatorsDoesNotRewriteEarlierInspectionAttribution()
    {
        var (store, source) = Setup();
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        var first = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "SHIFT-ONE", Operator, source);
        var next = Operator with { Id = "operator-2", UserName = "operator2", DisplayName = "Operator Two" };
        var second = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "SHIFT-TWO", next, source);
        Assert.Equal(Operator.Id, store.Get(first.Id)!.OperatorId);
        Assert.Equal(Operator.DisplayName, store.Get(first.Id)!.OperatorName);
        Assert.Equal(next.Id, store.Get(second.Id)!.OperatorId);
        Assert.Equal(next.DisplayName, store.Get(second.Id)!.OperatorName);
        Assert.Equal(2, store.PendingCount());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("QualityEngineer")]
    public async Task UnauthenticatedAndNonOperatorCallsDoNotCreateStartedRecords(string? role)
    {
        var (store, source) = Setup();
        var user = role is null ? null : Operator with { Roles = [role] };
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "UNAUTHORIZED", user!, source));
        Assert.Empty(store.ReadRecent());
        Assert.Equal(0, store.PendingCount());
        Assert.False(coordinator.IsFaulted);
    }

    [Fact]
    public async Task RealImageDetectionIsReturnedOnlyWithItsDurableArchive()
    {
        var (store, source) = Setup();
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        var result = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "SIM-001", Operator, source);
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
        var result = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "SIM-002", Operator, source);
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
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "SIM-003", Operator, source));
        Assert.True(coordinator.IsFaulted);
        var stored = Assert.Single(store.ReadRecent()).Record;
        Assert.Equal(QualityDecision.NotEvaluated, stored.Decision);
        Assert.Null(store.Get(stored.Id)!.TestedImage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "SIM-004", Operator, source));
        Assert.Single(store.ReadRecent());
    }

    [Fact]
    public async Task BusyStationDoesNotQueueOrStartAnotherInspection()
    {
        var (store, source) = Setup();
        var delayed = new PausedSource(source);
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        var first = coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "SIM-005", Operator, delayed);
        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "TEST-01", "SIM-006", Operator, source));
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
