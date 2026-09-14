using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Tests;

public sealed class BatchExecutionTests
{
    private static readonly CurrentUser Operator = new("operator-1", "operator", "First Operator", ["Operator"], null);
    private static readonly CurrentUser Other = new("operator-2", "other", "Second Operator", ["Operator"], null);

    private static (PublishedInspectionCoordinatorTests.Fixture Fixture, BatchDefinition Batch, InspectionCoordinator Coordinator) Setup()
    {
        var fixture = PublishedInspectionCoordinatorTests.Setup();
        var batch = new BatchDefinition(Guid.NewGuid(), "BATCH-001", "PCB-A", "640x640", 2, "ST-1",
            fixture.Loaded.VersionId, fixture.Loaded.BundleHash, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
        return (fixture, batch, new InspectionCoordinator(fixture.Store, new ClassicalSettings()));
    }

    private static BatchPackage Package(PublishedInspectionCoordinatorTests.Fixture fixture, BatchDefinition batch,
        BatchStatus status, FirstArticleApproval? approval = null) => new(batch, status, approval, fixture.Version);

    private static IImageSource Source(PublishedInspectionCoordinatorTests.Fixture fixture, bool constructed = false) =>
        new PublishedReplayImageSource(fixture.Folder, new ReplaySample("controlled-1", "tested.png", "reference.png"),
            constructed ? fixture.Loaded.GetReferenceBytes("controlled-1") : null);

    private static BatchExecutionSession Session(BatchDefinition batch, FirstArticleApproval approval, CurrentUser actor,
        DateTimeOffset? issued = null, DateTimeOffset? expires = null) => new(Guid.NewGuid(), batch.Id, batch.StationId,
            batch.RecipeBundleHash, approval.InspectionId, actor.Id, actor.DisplayName,
            issued ?? DateTimeOffset.UtcNow.AddMinutes(-1), expires ?? DateTimeOffset.UtcNow.AddHours(1));

    private static async Task<FirstArticleApproval> ApproveFixtureAsync(PublishedInspectionCoordinatorTests.Fixture fixture,
        BatchDefinition batch, InspectionCoordinator coordinator)
    {
        coordinator.UseBatch(Package(fixture, batch, BatchStatus.AwaitingFirstArticle), fixture.Loaded);
        var first = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId, "FIRST-PASS", Operator,
            Source(fixture, constructed: true));
        Assert.Equal(QualityDecision.Pass, first.Decision);
        var approval = new FirstArticleApproval(batch.Id, first.Id, "quality", "Fixture Quality", DateTimeOffset.UtcNow);
        coordinator.UseBatch(Package(fixture, batch, BatchStatus.Approved, approval), fixture.Loaded);
        return approval;
    }

    [Fact]
    public async Task FirstArticleCanRetryAfterRealFailButPassWaitsForCentralApproval()
    {
        var (f, batch, coordinator) = Setup();
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.FirstArticle,
            batch.StationId, "NOT-DOWNLOADED", Operator, Source(f)));
        Assert.Empty(f.Store.ReadRecent());
        Assert.False(coordinator.IsFaulted);
        coordinator.UseBatch(Package(f, batch, BatchStatus.AwaitingFirstArticle), f.Loaded);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "NOT-STARTED", Operator, Source(f)));
        Assert.Empty(f.Store.ReadRecent());
        var failed = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId, "FIRST-FAIL", Operator, Source(f));
        Assert.Equal(InspectionExecution.Completed, failed.ExecutionStatus);
        Assert.Equal(QualityDecision.Fail, failed.Decision);
        Assert.Equal(batch.Id, failed.BatchId);
        var passed = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId, "FIRST-PASS", Operator,
            Source(f, constructed: true));
        Assert.Equal(QualityDecision.Pass, passed.Decision);
        Assert.Equal("ConstructedNormal", passed.SourceKind);
        Assert.Equal(f.Reference, passed.TestedImage);
        Assert.Equal(f.Reference, passed.ReferenceImage);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.FirstArticle,
            batch.StationId, "DUPLICATE-PASS", Operator, Source(f, constructed: true)));
        Assert.Equal(2, f.Store.ReadRecent().Count);
        Assert.Equal(2, f.Store.PendingCount());
        Assert.Equal(1, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.False(coordinator.IsFaulted);
    }

    [Fact]
    public async Task ApprovedOnlineSessionAssignsPersistentProductionSequenceAndRetainsFullEvidence()
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "NO-SESSION", Operator, Source(f)));
        Assert.Equal(1, f.Store.PendingCount());
        var session = Session(batch, approval, Operator);
        coordinator.StartBatch(session, null);
        var first = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "PROD-1", Operator, Source(f));
        Assert.Equal(1, first.ProductionSequence);
        Assert.Equal(session.Id, first.ExecutionSessionId);
        Assert.Equal(batch.Id, first.BatchId);
        Assert.Equal(f.Version.BundleHash, PublishedRecipeTransfer.Hash(
            System.Text.Json.JsonSerializer.Deserialize<PublishedRecipeVersion>(first.RecipeJson,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Bundle));
        var reopened = new LocalInspectionStore(f.Store.DatabasePath);
        reopened.Initialize();
        Assert.Equal(2, reopened.ReadActiveBatch()!.NextProductionSequence);
        Assert.Equal(session.Id, reopened.ReadActiveBatch()!.Session!.Id);
        Assert.Equal(f.Reference, reopened.Get(first.Id)!.ReferenceImage);
        Assert.Equal(f.Tested, reopened.Get(first.Id)!.TestedImage);
        Assert.Equal(2, reopened.PendingCount());
        coordinator.UseBatch(Package(f, batch, BatchStatus.AwaitingFirstArticle), f.Loaded);
        Assert.Equal(2, reopened.ReadActiveBatch()!.NextProductionSequence);
        var second = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "PROD-2", Operator,
            Source(f, constructed: true));
        Assert.Equal(2, second.ProductionSequence);
        Assert.Equal("ConstructedNormal", second.SourceKind);
        Assert.Equal(f.Reference, second.TestedImage);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "OVER-PLAN", Operator, Source(f)));
        Assert.Equal(3, reopened.PendingCount());
        Assert.Equal(3, reopened.ReadActiveBatch()!.NextProductionSequence);
        Assert.False(coordinator.IsFaulted);
    }

    [Fact]
    public async Task WrongOperatorExpiredSessionAndModeSwitchCannotBypassActiveBatch()
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        Assert.Throws<InspectionRejectedException>(() => coordinator.StartBatch(Session(batch, approval, Operator,
            DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1)), null));
        coordinator.StartBatch(Session(batch, approval, Operator), null);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "WRONG-OPERATOR", Other, Source(f)));
        Assert.Throws<InspectionRejectedException>(() => coordinator.UseDevelopmentRecipe(new ClassicalSettings()));
        Assert.Throws<InspectionRejectedException>(() => coordinator.UsePublishedRecipe(f.Loaded));
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay,
            batch.StationId, "ENGINEERING-BYPASS", Operator, Source(f)));
        coordinator.EndOperatorSession();
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "AFTER-SIGNOUT", Operator, Source(f)));
        Assert.Single(f.Store.ReadRecent());
        Assert.Equal(1, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.Null(f.Store.ReadActiveBatch()!.Session);
        Assert.False(coordinator.IsFaulted);
    }

    [Fact]
    public async Task StartedInsertFailureRollsBackSequenceAndLatchesStorageFault()
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        coordinator.StartBatch(Session(batch, approval, Operator), null);
        using (var connection = new SqliteConnection($"Data Source={f.Store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_started BEFORE INSERT ON Inspections BEGIN SELECT RAISE(ABORT, 'fixture insert failure'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "START-FAIL", Operator, Source(f)));
        Assert.True(coordinator.IsFaulted);
        Assert.Equal(1, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.Single(f.Store.ReadRecent());
        Assert.Throws<InvalidOperationException>(() => coordinator.UseDevelopmentRecipe(new ClassicalSettings()));
        Assert.Throws<InvalidOperationException>(() => coordinator.UseBatch(Package(f, batch, BatchStatus.InProgress, approval), f.Loaded));
    }

    [Fact]
    public async Task ExpiredStartedSessionRejectsNewProductionWithoutConsumingSequence()
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        coordinator.StartBatch(Session(batch, approval, Operator, expires: DateTimeOffset.UtcNow.AddMilliseconds(500)), null);
        await Task.Delay(700);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "EXPIRED", Operator, Source(f)));
        Assert.Equal(1, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.Single(f.Store.ReadRecent());
        Assert.False(coordinator.IsFaulted);
        coordinator.StartBatch(Session(batch, approval, Operator), null);
        var accepted = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "RESTARTED", Operator, Source(f));
        Assert.Equal(1, accepted.ProductionSequence);
        Assert.Equal(2, f.Store.PendingCount());
    }

    [Fact]
    public async Task FailedSessionClearRetainsOldSessionButStopsAllFurtherInput()
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        var session = Session(batch, approval, Operator);
        coordinator.StartBatch(session, null);
        using (var connection = new SqliteConnection($"Data Source={f.Store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_session_clear BEFORE UPDATE OF ExecutionSession ON ActiveBatch WHEN NEW.ExecutionSession IS NULL BEGIN SELECT RAISE(ABORT, 'fixture disk failure'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => coordinator.EndOperatorSession());
        Assert.True(coordinator.IsFaulted);
        Assert.Equal(session.Id, f.Store.ReadActiveBatch()!.Session!.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "AFTER-FAILED-SIGNOUT", Operator, Source(f)));
        Assert.Single(f.Store.ReadRecent());
        Assert.Equal(1, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.Throws<InvalidOperationException>(() => coordinator.UseDevelopmentRecipe(new ClassicalSettings()));
    }

    [Fact]
    public async Task ResumePayloadReloadsWithItsSessionAndNullExplicitlyRemovesPreviousPayload()
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        var session = Session(batch, approval, Operator);
        byte[] protectedBytes = [2, 4, 6, 8];
        coordinator.StartBatch(session, protectedBytes);
        protectedBytes[0] = 99;
        var reopened = new LocalInspectionStore(f.Store.DatabasePath);
        var resume = reopened.ReadBatchResume()!;
        Assert.Equal(session, resume.Batch.Session);
        Assert.Equal(BatchStatus.InProgress, resume.Batch.Status);
        Assert.Equal(new byte[] { 2, 4, 6, 8 }, resume.ProtectedPayload);
        coordinator.UseBatch(Package(f, batch, BatchStatus.InProgress, approval), f.Loaded);
        Assert.Equal(session, reopened.ReadBatchResume()!.Batch.Session);
        var replacement = Session(batch, approval, Operator);
        coordinator.StartBatch(replacement, null);
        Assert.Null(reopened.ReadBatchResume());
        Assert.Equal(0, ResumeRowCount(reopened));
        Assert.Equal(replacement, reopened.ReadActiveBatch()!.Session);
        Assert.Single(reopened.ReadRecent());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeWriteFailureRollsBackSessionStatusAndPayload(bool existingSession)
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        var previous = Session(batch, approval, Operator);
        if (existingSession) coordinator.StartBatch(previous, [1, 2, 3]);
        Sql(f.Store, "CREATE TRIGGER reject_resume BEFORE INSERT ON OfflineBatchResume BEGIN SELECT RAISE(ABORT,'resume fixture failure'); END;");
        Assert.Throws<SqliteException>(() => coordinator.StartBatch(Session(batch, approval, Operator), [9, 8]));
        Assert.True(coordinator.IsFaulted);
        var batchAfter = f.Store.ReadActiveBatch()!;
        Assert.Equal(existingSession ? BatchStatus.InProgress : BatchStatus.Approved, batchAfter.Status);
        Assert.Equal(existingSession ? previous : null, batchAfter.Session);
        if (existingSession) Assert.Equal(new byte[] { 1, 2, 3 }, f.Store.ReadBatchResume()!.ProtectedPayload);
        else Assert.Null(f.Store.ReadBatchResume());
        Assert.Single(f.Store.ReadRecent());
    }

    [Fact]
    public async Task ResumeDeleteFailureRollsBackSessionClearThenSuccessfulClearRemovesBoth()
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        var session = Session(batch, approval, Operator);
        coordinator.StartBatch(session, [7, 6]);
        Sql(f.Store, "CREATE TRIGGER reject_resume_delete BEFORE DELETE ON OfflineBatchResume BEGIN SELECT RAISE(ABORT,'resume delete failure'); END;");
        Assert.Throws<SqliteException>(() => coordinator.EndOperatorSession());
        Assert.True(coordinator.IsFaulted);
        Assert.Equal(session, f.Store.ReadActiveBatch()!.Session);
        Assert.Equal(new byte[] { 7, 6 }, f.Store.ReadBatchResume()!.ProtectedPayload);
        Sql(f.Store, "DROP TRIGGER reject_resume_delete;");
        f.Store.ClearExecutionSession();
        Assert.Null(f.Store.ReadActiveBatch()!.Session);
        Assert.Null(f.Store.ReadBatchResume());
        Assert.Equal(0, ResumeRowCount(f.Store));
        Assert.Single(f.Store.ReadRecent());
        Assert.Equal(1, f.Store.PendingCount());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingOrSelectingAnotherBatchClearsTheOldResumePayload(bool selectAnother)
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        coordinator.StartBatch(Session(batch, approval, Operator), [5]);
        // This test supplies the terminal batch state; closing workflow is a separate feature.
        Sql(f.Store, "UPDATE CachedBatches SET Status='Closed';");
        if (selectAnother)
        {
            var next = batch with { Id = Guid.NewGuid(), BatchNumber = "NEXT-BATCH" };
            coordinator.UseBatch(Package(f, next, BatchStatus.AwaitingFirstArticle), f.Loaded);
            Assert.Equal(next.Id, f.Store.ReadActiveBatch()!.Batch.Id);
            Assert.Null(f.Store.ReadActiveBatch()!.Session);
        }
        else
        {
            coordinator.UseDevelopmentRecipe(new ClassicalSettings());
            Assert.Null(f.Store.ReadActiveBatch());
        }
        Assert.Null(f.Store.ReadBatchResume());
        Assert.Equal(0, ResumeRowCount(f.Store));
        Assert.Single(f.Store.ReadRecent());
    }

    [Fact]
    public async Task ClearingResumeAuthorizationDoesNotRemoveHeldPlcResultOrItsDuplicateIdentity()
    {
        var (f, batch, coordinator) = Setup();
        var approval = await ApproveFixtureAsync(f, batch, coordinator);
        coordinator.StartBatch(Session(batch, approval, Operator), [4, 3]);
        var identity = new InspectionIdentity(Guid.NewGuid(), 1);
        var result = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "PLC-RESUME",
            Operator, Source(f), identity: identity);
        var hash = InspectionTransfer.Hash(result);
        coordinator.EndOperatorSession();
        Assert.Null(f.Store.ReadBatchResume());
        Assert.Null(f.Store.ReadActiveBatch()!.Session);
        Assert.Equal(result.Id, f.Store.ReadUnacknowledgedPlc()!.Record.Id);
        File.Delete(Path.Combine(f.Folder, "tested.png"));
        var repeated = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "PLC-RESUME",
            Operator, Source(f), identity: identity);
        Assert.Equal(result.Id, repeated.Id);
        coordinator.ConfirmPlcAck(result.Id);
        Assert.Null(f.Store.ReadUnacknowledgedPlc());
        Assert.Equal(hash, InspectionTransfer.Hash(f.Store.Get(result.Id)!));
        Assert.Equal(2, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.Equal(2, f.Store.PendingCount());
    }

    private static void Sql(LocalInspectionStore store, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ResumeRowCount(LocalInspectionStore store)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM OfflineBatchResume;";
        return (long)command.ExecuteScalar()!;
    }
}
