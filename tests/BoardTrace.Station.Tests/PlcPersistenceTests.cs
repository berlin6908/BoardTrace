using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Tests;

public sealed class PlcPersistenceTests
{
    private static readonly CurrentUser Operator = new("plc-operator", "operator", "PLC Operator", ["Operator"], null);
    private static InspectionIdentity Identity() => new(Guid.NewGuid(), uint.MaxValue);
    private static IImageSource Source(PublishedInspectionCoordinatorTests.Fixture fixture) =>
        new ReplayImageSource(fixture.Folder, new ReplaySample("controlled-1", "tested.png", "reference.png"));
    private static InspectionRecord Started(InspectionIdentity? identity = null) => new()
    {
        Id = Guid.NewGuid(), StationId = "ST-1", ProductId = "PLC-ONE", Purpose = InspectionPurpose.EngineeringReplay,
        OperatorId = Operator.Id, OperatorName = Operator.DisplayName, SampleId = "controlled-1", SourceKind = "Replay",
        RecipeId = "engineering-fixture", RecipeJson = "{}", StartedAt = DateTimeOffset.UtcNow,
        ControllerSessionId = identity?.ControllerSessionId, TriggerSequence = identity?.TriggerSequence
    };
    private static void Sql(LocalInspectionStore store, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static async Task<(PublishedInspectionCoordinatorTests.Fixture F, BatchDefinition Batch, InspectionCoordinator Coordinator)> Production()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var batch = new BatchDefinition(Guid.NewGuid(), "PLC-BATCH", "PCB", "TOP", 2, "ST-1", f.Loaded.VersionId,
            f.Loaded.BundleHash, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        coordinator.UseBatch(new(batch, BatchStatus.AwaitingFirstArticle, null, f.Version), f.Loaded);
        var first = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId, "FIRST", Operator,
            new PublishedReplayImageSource(f.Folder, new ReplaySample("controlled-1", "tested.png", "reference.png"), f.Reference));
        Assert.Equal(QualityDecision.Pass, first.Decision);
        var approval = new FirstArticleApproval(batch.Id, first.Id, "quality", "Fixture Quality", DateTimeOffset.UtcNow);
        coordinator.UseBatch(new(batch, BatchStatus.Approved, approval, f.Version), f.Loaded);
        coordinator.StartBatch(new(Guid.NewGuid(), batch.Id, batch.StationId, batch.RecipeBundleHash, first.Id,
            Operator.Id, Operator.DisplayName, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)), null);
        return (f, batch, coordinator);
    }
    private static InspectionRecord ProductionStarted(BatchDefinition batch, InspectionIdentity? identity = null) =>
        Started(identity) with { Purpose = InspectionPurpose.Production, RecipeId = batch.RecipeVersionId.ToString("D") };

    [Fact]
    public async Task StartedNotificationFollowsCommitAndPrecedesCaptureWhileAckRequiresTerminalResult()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        var identity = Identity();
        var notified = new TaskCompletionSource<InspectionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ObservedSource(Source(f), async () =>
        {
            Assert.True(notified.Task.IsCompletedSuccessfully);
            await captureRelease.Task;
        });
        var running = coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "PLC-ONE", Operator, source,
            identity: identity, startedCommitted: record =>
            {
                Assert.True(coordinator.IsBusy);
                Assert.Equal(InspectionExecution.Started, f.Store.Get(record.Id)!.ExecutionStatus);
                Assert.Equal(record.Id, f.Store.ReadPlcTrigger(identity)!.Record.Id);
                Assert.Equal(0, f.Store.PendingCount());
                notified.TrySetResult(record);
            });
        var started = await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InspectionRejectedException>(() => f.Store.ConfirmPlcAck(started.Id));
        Assert.Null(f.Store.ReadPlcTrigger(identity)!.AckAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay,
            "ST-1", "MANUAL-WHILE-BUSY", Operator, Source(f)));
        captureRelease.TrySetResult();
        var completed = await running;
        Assert.Equal(InspectionExecution.Completed, completed.ExecutionStatus);
        Assert.Equal(1, source.Captures);
        Assert.False(coordinator.IsBusy);
        var metadata = f.Store.ReadUnacknowledgedPlc()!.Record;
        Assert.Equal(completed.Id, metadata.Id);
        Assert.Null(metadata.TestedImage); Assert.Null(metadata.ReferenceImage);
        Assert.NotEmpty(f.Store.Get(completed.Id)!.TestedImage!);
    }

    [Fact]
    public async Task RepeatedPhysicalTriggerReturnsOriginalWithoutCaptureOrAnotherProductionSequence()
    {
        var (f, batch, coordinator) = await Production();
        var identity = Identity();
        var source = new ObservedSource(Source(f));
        var notifications = 0;
        Task<InspectionRecord> Run(string product = "PLC-ONE", IImageSource? input = null) => coordinator.InspectAsync(
            InspectionPurpose.Production, batch.StationId, product, Operator, input ?? source,
            identity: identity, startedCommitted: _ => notifications++);
        var first = await Run();
        Assert.Equal(1, first.ProductionSequence);
        var duplicate = await Run();
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(1, notifications); Assert.Equal(1, source.Captures);
        Assert.Equal(2, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.Equal(2, f.Store.ReadRecent().Count);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => Run("ALTERED-PRODUCT"));
        await Assert.ThrowsAsync<InspectionRejectedException>(() => Run(input: new NamedSource(source, "foreign-sample")));
        Assert.False(coordinator.IsFaulted);
        coordinator.ConfirmPlcAck(first.Id);
        var ack = f.Store.ReadPlcTrigger(identity)!.AckAt;
        Assert.NotNull(ack);
        Assert.Equal(first.Id, (await Run()).Id);
        coordinator.ConfirmPlcAck(first.Id);
        Assert.Equal(ack, f.Store.ReadPlcTrigger(identity)!.AckAt);
        Assert.Equal(1, source.Captures);
        var next = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "PLC-TWO", Operator, source,
            identity: new InspectionIdentity(Guid.NewGuid(), 1));
        Assert.Equal(2, next.ProductionSequence);
        Assert.NotEqual(first.Id, next.Id);
    }

    [Fact]
    public async Task UnacknowledgedResultGatesManualAndRecipeChangesIndependentlyOfCentralUpload()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        var identity = Identity();
        var result = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "PLC-ONE", Operator, Source(f), identity: identity);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay,
            "ST-1", "MANUAL", Operator, Source(f)));
        Assert.Throws<InspectionRejectedException>(() => f.Store.BeginAccepted(Started(Identity()), null));
        Assert.Throws<InspectionRejectedException>(() => coordinator.UseDevelopmentRecipe(new ClassicalSettings()));
        Assert.Throws<InspectionRejectedException>(() => coordinator.UsePublishedRecipe(f.Loaded));
        var batch = new BatchDefinition(Guid.NewGuid(), "OTHER", "PCB", "TOP", 2, "ST-1", f.Loaded.VersionId,
            f.Loaded.BundleHash, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
        Assert.Throws<InspectionRejectedException>(() => coordinator.UseBatch(new(batch, BatchStatus.AwaitingFirstArticle, null, f.Version), f.Loaded));
        coordinator.EndOperatorSession();
        Assert.NotNull(f.Store.ReadUnacknowledgedPlc());
        var hash = InspectionTransfer.Hash(result);
        f.Store.ConfirmUploaded(new(result.Id, hash, DateTimeOffset.UtcNow));
        Assert.Equal(0, f.Store.PendingCount());
        Assert.NotNull(f.Store.ReadUnacknowledgedPlc());
        Assert.Throws<InspectionRejectedException>(() => coordinator.ConfirmPlcAck(Guid.NewGuid()));
        Assert.False(coordinator.IsFaulted);
        coordinator.ConfirmPlcAck(result.Id);
        Assert.Null(f.Store.ReadUnacknowledgedPlc());
        Assert.Equal(hash, InspectionTransfer.Hash(f.Store.Get(result.Id)!));
        coordinator.UsePublishedRecipe(f.Loaded);
        var manual = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "AFTER-ACK", Operator, Source(f));
        Assert.Null(manual.ControllerSessionId); Assert.Null(manual.TriggerSequence);
    }

    [Fact]
    public async Task PhysicalIdentityInsertFailureRollsBackStartedAndProductionSequenceBeforeNotification()
    {
        var (f, batch, coordinator) = await Production();
        var identity = Identity();
        var source = new ObservedSource(Source(f)); var notified = false;
        Sql(f.Store, "CREATE TRIGGER reject_physical BEFORE INSERT ON PlcTriggers BEGIN SELECT RAISE(ABORT,'fixture storage failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId,
            "PLC-ONE", Operator, source, identity: identity, startedCommitted: _ => notified = true));
        Assert.True(coordinator.IsFaulted); Assert.False(coordinator.IsBusy);
        Assert.False(notified); Assert.Equal(0, source.Captures);
        Assert.Null(f.Store.ReadPlcTrigger(identity));
        Assert.Equal(1, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.Single(f.Store.ReadRecent());
    }

    [Fact]
    public async Task ConcurrentDuplicateStartsReserveOneRecordAndOneProductionSequence()
    {
        var (f, batch, _) = await Production();
        var identity = Identity();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            new LocalInspectionStore(f.Store.DatabasePath).BeginAccepted(ProductionStarted(batch, identity), batch.RecipeBundleHash))));
        Assert.Single(results, item => item.IsNew);
        Assert.Equal(results[0].Record.Id, results[1].Record.Id);
        Assert.Equal(1, results[0].Record.ProductionSequence);
        Assert.Equal(2, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Assert.Equal(2, f.Store.ReadRecent().Count);
    }

    [Fact]
    public async Task StartupRecoveryAtomicallyRetainsIdentityAndConsumedQuantityWithoutReplaying()
    {
        var (f, batch, _) = await Production();
        var originalFirst = f.Store.ReadRecent().Single().Record;
        var manual = f.Store.BeginAccepted(ProductionStarted(batch), batch.RecipeBundleHash).Record;
        var identity = Identity();
        var physical = f.Store.BeginAccepted(ProductionStarted(batch, identity), batch.RecipeBundleHash).Record;
        Sql(f.Store, $"CREATE TRIGGER reject_recovery BEFORE INSERT ON UploadState WHEN NEW.InspectionId='{physical.Id}' BEGIN SELECT RAISE(ABORT,'fixture recovery failure'); END;");
        var broken = new InspectionCoordinator(f.Store, new ClassicalSettings());
        Assert.Throws<SqliteException>(() => broken.RecoverInterrupted());
        Assert.True(broken.IsFaulted);
        Assert.Equal(InspectionExecution.Started, f.Store.Get(manual.Id)!.ExecutionStatus);
        Assert.Equal(InspectionExecution.Started, f.Store.Get(physical.Id)!.ExecutionStatus);
        Assert.Equal(1, f.Store.PendingCount());
        Sql(f.Store, "DROP TRIGGER reject_recovery;");
        var reopened = new LocalInspectionStore(f.Store.DatabasePath); reopened.Initialize();
        Assert.Equal(InspectionExecution.Started, reopened.Get(physical.Id)!.ExecutionStatus);
        var recovered = new InspectionCoordinator(reopened, new ClassicalSettings());
        Assert.Equal(2, recovered.RecoverInterrupted());
        Assert.Equal(0, recovered.RecoverInterrupted());
        Assert.Equal(3, reopened.PendingCount());
        foreach (var old in new[] { manual, physical })
        {
            var item = reopened.Get(old.Id)!;
            Assert.Equal(InspectionExecution.Interrupted, item.ExecutionStatus);
            Assert.Equal(QualityDecision.NotEvaluated, item.Decision);
            Assert.Equal(old.ProductionSequence, item.ProductionSequence);
            Assert.Equal(old.BatchId, item.BatchId); Assert.Equal(old.ExecutionSessionId, item.ExecutionSessionId);
            Assert.Equal(old.OperatorId, item.OperatorId); Assert.Equal(old.RecipeId, item.RecipeId);
            Assert.Equal(old.ControllerSessionId, item.ControllerSessionId); Assert.Equal(old.TriggerSequence, item.TriggerSequence);
            Assert.NotNull(item.CompletedAt); Assert.NotNull(item.Error);
            Assert.Null(item.TestedImage); Assert.Null(item.ReferenceImage); Assert.Empty(item.Defects);
        }
        Assert.Equal(InspectionExecution.Completed, reopened.Get(originalFirst.Id)!.ExecutionStatus);
        Assert.Equal(physical.Id, reopened.ReadUnacknowledgedPlc()!.Record.Id);
        Assert.Equal(3, reopened.ReadActiveBatch()!.NextProductionSequence);
        recovered.ConfirmPlcAck(physical.Id);
        Assert.Throws<InspectionRejectedException>(() => reopened.BeginAccepted(ProductionStarted(batch), batch.RecipeBundleHash));
        Assert.Equal(3, reopened.ReadActiveBatch()!.NextProductionSequence);
    }

    [Fact]
    public async Task AckPersistenceFailureRetainsResultAndLatchesCoordinatorFault()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        var identity = Identity();
        var result = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "PLC-ONE", Operator, Source(f), identity: identity);
        Sql(f.Store, "CREATE TRIGGER reject_ack BEFORE UPDATE OF AckAt ON PlcTriggers BEGIN SELECT RAISE(ABORT,'fixture ACK disk failure'); END;");
        Assert.Throws<SqliteException>(() => coordinator.ConfirmPlcAck(result.Id));
        Assert.True(coordinator.IsFaulted);
        Assert.Null(f.Store.ReadPlcTrigger(identity)!.AckAt);
        Assert.Equal(result.Id, f.Store.ReadUnacknowledgedPlc()!.Record.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "NEXT", Operator, Source(f)));
        Assert.Equal(1, f.Store.PendingCount());
        Assert.Equal(InspectionTransfer.Hash(result), InspectionTransfer.Hash(f.Store.Get(result.Id)!));
    }

    [Fact]
    public async Task StartupMayRestoreOnlyItsCachedRecipeWithoutBypassingTheHeldResult()
    {
        var (f, batch, coordinator) = await Production();
        var first = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "PLC-ONE", Operator,
            Source(f), identity: Identity());
        var before = f.Store.ReadActiveBatch();
        var reopened = new InspectionCoordinator(new LocalInspectionStore(f.Store.DatabasePath), new ClassicalSettings());
        Assert.Equal(0, reopened.RecoverInterrupted());
        reopened.RestoreCachedBatchRecipe(f.Loaded);
        Assert.Equal(before, f.Store.ReadActiveBatch());
        Assert.Equal(first.Id, f.Store.ReadUnacknowledgedPlc()!.Record.Id);
        var other = PublishedInspectionCoordinatorTests.Setup();
        Assert.Throws<InspectionRejectedException>(() => reopened.RestoreCachedBatchRecipe(other.Loaded));
        await Assert.ThrowsAsync<InspectionRejectedException>(() => reopened.InspectAsync(InspectionPurpose.Production,
            batch.StationId, "BEFORE-ACK", Operator, Source(f)));
        Assert.False(reopened.IsFaulted);
        reopened.ConfirmPlcAck(first.Id);
        var next = await reopened.InspectAsync(InspectionPurpose.Production, batch.StationId, "AFTER-ACK", Operator,
            Source(f), identity: Identity());
        Assert.Equal(batch.RecipeVersionId.ToString("D"), next.RecipeId);
        Assert.Equal(2, next.ProductionSequence);
        Assert.Equal(new double[] { 466, 276, 489, 302 }, Assert.Single(next.Defects).Box);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing-sequence")]
    [InlineData("missing-session")]
    [InlineData("zero")]
    public void InvalidPhysicalIdentityNeverCreatesStarted(string invalid)
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var record = Started(Identity());
        record = invalid switch
        {
            "empty" => record with { ControllerSessionId = Guid.Empty },
            "missing-sequence" => record with { TriggerSequence = null },
            "missing-session" => record with { ControllerSessionId = null },
            _ => record with { TriggerSequence = 0 }
        };
        Assert.Throws<InspectionRejectedException>(() => f.Store.BeginAccepted(record, null));
        Assert.Empty(f.Store.ReadRecent()); Assert.Null(f.Store.ReadUnacknowledgedPlc());
    }

    [Fact]
    public void PhysicalIdentityCannotBeReplacedWhenFinishingItsRecord()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var identity = Identity();
        var started = f.Store.BeginAccepted(Started(identity), null).Record;
        var interrupted = started with { ExecutionStatus = InspectionExecution.Interrupted,
            CompletedAt = DateTimeOffset.UtcNow, Error = "explicit test cancellation" };
        Assert.Throws<InvalidOperationException>(() => f.Store.Complete(interrupted with { ControllerSessionId = Guid.NewGuid() }));
        Assert.Equal(InspectionExecution.Started, f.Store.Get(started.Id)!.ExecutionStatus);
        Assert.Equal(0, f.Store.PendingCount());
        f.Store.Complete(interrupted);
        Assert.NotEqual(InspectionTransfer.Hash(interrupted), InspectionTransfer.Hash(interrupted with { TriggerSequence = 1 }));
        f.Store.ConfirmPlcAck(started.Id);
        Assert.NotNull(f.Store.ReadPlcTrigger(identity)!.AckAt);
    }

    private sealed class ObservedSource(IImageSource inner, Func<Task>? beforeCapture = null) : IImageSource
    {
        public int Captures { get; private set; }
        public string SampleId => inner.SampleId;
        public string SourceKind => inner.SourceKind;
        public async Task<CapturedPair> CaptureAsync(CancellationToken token)
        {
            Captures++;
            if (beforeCapture is not null) await beforeCapture();
            return await inner.CaptureAsync(token);
        }
    }
    private sealed class NamedSource(IImageSource inner, string sampleId) : IImageSource
    {
        public string SampleId => sampleId;
        public string SourceKind => inner.SourceKind;
        public Task<CapturedPair> CaptureAsync(CancellationToken token) => inner.CaptureAsync(token);
    }
}
