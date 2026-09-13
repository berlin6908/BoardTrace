using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Tests;

public sealed class LocalInspectionStoreTests
{
    private static LocalInspectionStore CreateStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString(), "station.db");
        var store = new LocalInspectionStore(path);
        store.Initialize();
        return store;
    }

    private static InspectionRecord Started() => new()
    {
        Id = Guid.NewGuid(), StationId = "TEST-01", ProductId = "SIM-1", SampleId = "sample-1", Purpose = InspectionPurpose.EngineeringReplay,
        OperatorId = "operator-1", OperatorName = "Operator One",
            SourceKind = "Replay", RecipeId = "test-recipe", RecipeJson = "{}", StartedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void ReopenedStoreReturnsTheCommittedImagesResultAndOutbox()
    {
        var store = CreateStore();
        var started = Started();
        store.Begin(started);
        store.Complete(started with
        {
            ExecutionStatus = InspectionExecution.Completed, Decision = QualityDecision.Fail,
            CompletedAt = DateTimeOffset.UtcNow, TestedImage = [1, 2, 3], ReferenceImage = [4, 5, 6],
            Defects = [new DefectBox([12, 20, 33, 45], null, 1, 25)]
        });

        var reopened = new LocalInspectionStore(store.DatabasePath);
        var saved = Assert.IsType<InspectionRecord>(reopened.Get(started.Id));
        Assert.Equal(QualityDecision.Fail, saved.Decision);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved.TestedImage);
        Assert.Equal(new byte[] { 4, 5, 6 }, saved.ReferenceImage);
        Assert.Equal(new double[] { 12, 20, 33, 45 }, Assert.Single(saved.Defects).Box);
        Assert.Equal(1, reopened.PendingCount());
        Assert.True(Assert.Single(reopened.ReadRecent()).PendingUpload);
    }

    [Fact]
    public void OutboxWriteFailureRollsBackImagesAndQualityResultTogether()
    {
        var store = CreateStore();
        var started = Started();
        store.Begin(started);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_outbox BEFORE INSERT ON UploadState BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => store.Complete(started with
        {
            ExecutionStatus = InspectionExecution.Completed, Decision = QualityDecision.Pass,
            CompletedAt = DateTimeOffset.UtcNow, TestedImage = [1], ReferenceImage = [2]
        }));

        var retained = Assert.IsType<InspectionRecord>(store.Get(started.Id));
        Assert.Equal(InspectionExecution.Started, retained.ExecutionStatus);
        Assert.Equal(QualityDecision.NotEvaluated, retained.Decision);
        Assert.Null(retained.TestedImage);
        Assert.Null(retained.ReferenceImage);
        Assert.Equal(0, store.PendingCount());
    }

    [Fact]
    public void CompletedRecordCannotBeOverwritten()
    {
        var store = CreateStore();
        var started = Started();
        store.Begin(started);
        var completed = started with
        {
            ExecutionStatus = InspectionExecution.Completed, Decision = QualityDecision.Fail,
            CompletedAt = DateTimeOffset.UtcNow, TestedImage = [1], ReferenceImage = [2]
        };
        store.Complete(completed);

        Assert.Throws<InvalidOperationException>(() => store.Complete(completed with { Decision = QualityDecision.Pass }));
        Assert.Equal(QualityDecision.Fail, store.Get(started.Id)!.Decision);
        Assert.Equal(1, store.PendingCount());
    }
}
