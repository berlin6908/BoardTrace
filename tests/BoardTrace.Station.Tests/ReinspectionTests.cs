using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Tests;

public sealed class ReinspectionTests
{
    private static readonly CurrentUser Actor = new("operator", "operator", "Fixture Operator", ["Operator"], null);

    private sealed record Fixture(PublishedInspectionCoordinatorTests.Fixture Images, InspectionCoordinator Coordinator,
        BatchDefinition Batch, BatchExecutionSession Session, InspectionRecord Original, ReworkOrder Order)
    {
        public LocalInspectionStore Store => Images.Store;
        public IImageSource Source() => new PublishedReplayImageSource(Images.Folder, new ReplaySample("controlled-1", "tested.png", "reference.png"));
        public InspectionRecord Started(InspectionIdentity? identity = null) => Original with
        {
            Id = Guid.NewGuid(), Purpose = InspectionPurpose.Reinspection, ReworkOrderId = Order.Id,
            ProductionSequence = null, ExecutionStatus = InspectionExecution.Started, Decision = QualityDecision.NotEvaluated,
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = null, TestedImage = null, ReferenceImage = null,
            Defects = [], Diagnostics = new Dictionary<string, double>(), Width = 0, Height = 0, DetectionMs = null,
            ControllerSessionId = identity?.ControllerSessionId, TriggerSequence = identity?.TriggerSequence
        };
        public void Select()
        {
            Store.CacheReworkOrders(Batch.Id, [Order]);
            Coordinator.UseReworkOrder(Order.Id);
        }
    }

    private static async Task<Fixture> Setup(bool physicalOriginal = false)
    {
        var images = PublishedInspectionCoordinatorTests.Setup();
        var batch = new BatchDefinition(Guid.NewGuid(), "REWORK-FIXTURE", "PCB", "640x640", 1, "ST-1",
            images.Loaded.VersionId, images.Loaded.BundleHash, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
        var coordinator = new InspectionCoordinator(images.Store, new ClassicalSettings());
        coordinator.UseBatch(new BatchPackage(batch, BatchStatus.AwaitingFirstArticle, null, images.Version), images.Loaded);
        var first = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId, "FIRST", Actor,
            new PublishedReplayImageSource(images.Folder, new ReplaySample("controlled-1", "tested.png", "reference.png"), images.Reference));
        Assert.Equal(QualityDecision.Pass, first.Decision);
        var approval = new FirstArticleApproval(batch.Id, first.Id, "quality", "Fixture Quality", DateTimeOffset.UtcNow);
        coordinator.UseBatch(new BatchPackage(batch, BatchStatus.Approved, approval, images.Version), images.Loaded);
        var session = new BatchExecutionSession(Guid.NewGuid(), batch.Id, batch.StationId, batch.RecipeBundleHash, first.Id,
            Actor.Id, Actor.DisplayName, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), images.Store.ReadActiveBatch()!.ArchiveId);
        coordinator.StartBatch(session, [1, 2, 3]);
        var original = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "PCB-ORIGINAL", Actor,
            new PublishedReplayImageSource(images.Folder, new ReplaySample("controlled-1", "tested.png", "reference.png")),
            identity: physicalOriginal ? new InspectionIdentity(Guid.NewGuid(), 1) : null);
        Assert.Equal(QualityDecision.Fail, original.Decision);
        var order = new ReworkOrder(Guid.NewGuid(), original.Id, batch.Id, batch.StationId, original.ProductId, original.SampleId,
            batch.RecipeVersionId, batch.RecipeBundleHash, "Fixture reinspection after quality review", "quality", "Fixture Quality", DateTimeOffset.UtcNow);
        return new Fixture(images, coordinator, batch, session, original, order);
    }

    [Fact]
    public async Task FullBatchReinspectionPreservesOriginalImageHashAndDoesNotIncreaseProductionQuantity()
    {
        var f = await Setup();
        var originalHash = InspectionTransfer.Hash(f.Original);
        Assert.False(f.Store.ReadBatchResume()!.HasPendingRework);
        f.Select();
        Assert.True(f.Store.ReadBatchResume()!.HasPendingRework);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => f.Coordinator.InspectAsync(InspectionPurpose.Production,
            f.Batch.StationId, "ORDINARY-BYPASS", Actor, f.Source()));
        var result = await f.Coordinator.InspectAsync(InspectionPurpose.Reinspection, f.Batch.StationId, f.Order.ProductId, Actor, f.Source());
        Assert.NotEqual(f.Original.Id, result.Id);
        Assert.Equal(QualityDecision.Fail, result.Decision);
        Assert.Equal(f.Order.Id, result.ReworkOrderId);
        Assert.Equal(f.Session.Id, result.ExecutionSessionId);
        Assert.Equal(f.Batch.Id, result.BatchId);
        Assert.Equal(f.Original.RecipeJson, result.RecipeJson);
        Assert.Equal(f.Original.TestedImage, result.TestedImage);
        Assert.Equal(f.Original.ReferenceImage, result.ReferenceImage);
        Assert.Null(result.ProductionSequence);
        Assert.Equal(1, f.Store.ReadActiveBatch()!.AcceptedProductionCount);
        Assert.Equal(result.Id, f.Store.ReadSelectedReworkOrder()!.InspectionId);
        Assert.False(f.Store.ReadBatchResume()!.HasPendingRework);
        Assert.Empty(f.Store.ReadPendingReworkOrders(f.Batch.Id));
        await Assert.ThrowsAsync<InspectionRejectedException>(() => f.Coordinator.InspectAsync(InspectionPurpose.Reinspection,
            f.Batch.StationId, f.Order.ProductId, Actor, f.Source()));
        f.Coordinator.ExitReinspection();
        await Assert.ThrowsAsync<InspectionRejectedException>(() => f.Coordinator.InspectAsync(InspectionPurpose.Production,
            f.Batch.StationId, "OVER-PLAN", Actor, f.Source()));
        Assert.Equal(originalHash, InspectionTransfer.Hash(f.Store.Get(f.Original.Id)!));
        Assert.Equal(3, f.Store.PendingCount());
        Assert.False(f.Coordinator.IsFaulted);
    }

    [Fact]
    public async Task OldAckGatesModeAndRepeatedPhysicalTriggerNeverRecapturesConsumedReinspection()
    {
        var f = await Setup(physicalOriginal: true);
        f.Store.CacheReworkOrders(f.Batch.Id, [f.Order]);
        Assert.Throws<InspectionRejectedException>(() => f.Coordinator.UseReworkOrder(f.Order.Id));
        f.Coordinator.ConfirmPlcAck(f.Original.Id);
        f.Coordinator.UseReworkOrder(f.Order.Id);
        var source = new CountingSource(f.Source());
        var key = new InspectionIdentity(f.Original.ControllerSessionId!.Value, 2);
        var result = await f.Coordinator.InspectAsync(InspectionPurpose.Reinspection, f.Batch.StationId, f.Order.ProductId, Actor, source, identity: key);
        var hash = InspectionTransfer.Hash(result);
        Assert.Throws<InspectionRejectedException>(f.Coordinator.ExitReinspection);
        Assert.Throws<InspectionRejectedException>(() => f.Coordinator.UseReworkOrder(f.Order.Id));
        var repeat = await f.Coordinator.InspectAsync(InspectionPurpose.Reinspection, f.Batch.StationId, f.Order.ProductId, Actor, source, identity: key);
        Assert.Equal(result.Id, repeat.Id);
        Assert.Equal(1, source.Count);
        f.Coordinator.ConfirmPlcAck(result.Id);
        f.Coordinator.ExitReinspection();
        f.Coordinator.EndOperatorSession();
        repeat = await f.Coordinator.InspectAsync(InspectionPurpose.Production, f.Batch.StationId, f.Order.ProductId, Actor, source, identity: key);
        Assert.Equal(result.Id, repeat.Id);
        Assert.Equal(InspectionPurpose.Reinspection, repeat.Purpose);
        Assert.NotNull(f.Store.ReadPlcTrigger(key)!.AckAt);
        Assert.Equal(hash, InspectionTransfer.Hash(f.Store.Get(result.Id)!));
        Assert.Equal(1, source.Count);
        Assert.Equal(3, f.Store.ReadRecent().Count);
    }

    [Fact]
    public async Task StartedRecoveryKeepsConsumedTicketWithoutImagesOrAnotherAttempt()
    {
        var f = await Setup();
        f.Select();
        var key = new InspectionIdentity(Guid.NewGuid(), 7);
        var accepted = f.Store.BeginAccepted(f.Started(key), f.Images.Loaded.BundleHash);
        Assert.True(accepted.IsNew);
        var reopened = new LocalInspectionStore(f.Store.DatabasePath);
        reopened.Initialize();
        var coordinator = new InspectionCoordinator(reopened, new ClassicalSettings());
        Assert.Equal(1, coordinator.RecoverInterrupted());
        coordinator.RestoreCachedBatchRecipe(new LocalRecipeStore(reopened.DatabasePath).Load(f.Images.Loaded.VersionId));
        var interrupted = reopened.Get(accepted.Record.Id)!;
        Assert.Equal(InspectionExecution.Interrupted, interrupted.ExecutionStatus);
        Assert.Equal(QualityDecision.NotEvaluated, interrupted.Decision);
        Assert.Equal(f.Order.Id, interrupted.ReworkOrderId);
        Assert.Null(interrupted.TestedImage); Assert.Null(interrupted.ReferenceImage);
        Assert.Null(interrupted.ProductionSequence);
        Assert.Equal(f.Session.ArchiveId, reopened.ReadActiveBatch()!.ArchiveId);
        Assert.Equal(interrupted.Id, reopened.ReadSelectedReworkOrder()!.InspectionId);
        var source = new CountingSource(f.Source());
        Assert.Equal(interrupted.Id, (await coordinator.InspectAsync(InspectionPurpose.Reinspection, f.Batch.StationId,
            f.Order.ProductId, Actor, source, identity: key)).Id);
        Assert.Equal(0, source.Count);
        coordinator.ConfirmPlcAck(interrupted.Id);
        await Assert.ThrowsAsync<InspectionRejectedException>(() => coordinator.InspectAsync(InspectionPurpose.Reinspection,
            f.Batch.StationId, f.Order.ProductId, Actor, source, identity: new InspectionIdentity(key.ControllerSessionId, 8)));
        Assert.Equal(0, coordinator.RecoverInterrupted());
        Assert.Equal(1, reopened.ReadActiveBatch()!.AcceptedProductionCount);
        Assert.False(reopened.ReadBatchResume()!.HasPendingRework);
        Assert.Equal(3, reopened.PendingCount());
    }

    [Fact]
    public async Task CachedTicketDoesNotBypassOperatorExpiryFixedInputsOrMissingSession()
    {
        var f = await Setup();
        f.Select();
        var wrongActor = Actor with { Id = "other" };
        await Assert.ThrowsAsync<InspectionRejectedException>(() => f.Coordinator.InspectAsync(InspectionPurpose.Reinspection,
            f.Batch.StationId, f.Order.ProductId, wrongActor, f.Source()));
        await Assert.ThrowsAsync<InspectionRejectedException>(() => f.Coordinator.InspectAsync(InspectionPurpose.Reinspection,
            f.Batch.StationId, "WRONG-PRODUCT", Actor, f.Source()));
        Assert.Throws<InspectionRejectedException>(() => f.Store.BeginAccepted(f.Started() with { SampleId = "wrong" }, f.Images.Loaded.BundleHash));
        Assert.Throws<InspectionRejectedException>(() => f.Store.BeginAccepted(f.Started() with { StartedAt = f.Session.ExpiresAt }, f.Images.Loaded.BundleHash));
        Assert.Throws<InspectionRejectedException>(() => f.Store.BeginAccepted(f.Started(), new string('0', 64)));
        Assert.Null(f.Store.ReadSelectedReworkOrder()!.InspectionId);
        Assert.Equal(2, f.Store.ReadRecent().Count);
        f.Coordinator.EndOperatorSession();
        Assert.Null(f.Store.ReadBatchResume());
        await Assert.ThrowsAsync<InspectionRejectedException>(() => f.Coordinator.InspectAsync(InspectionPurpose.Reinspection,
            f.Batch.StationId, f.Order.ProductId, Actor, f.Source()));
        f.Coordinator.StartBatch(f.Session, [4]);
        var accepted = await f.Coordinator.InspectAsync(InspectionPurpose.Reinspection, f.Batch.StationId, f.Order.ProductId, Actor, f.Source());
        Assert.Equal(InspectionExecution.Completed, accepted.ExecutionStatus);
        Assert.Equal(f.Session.Id, accepted.ExecutionSessionId);
        Assert.False(f.Coordinator.IsFaulted);
    }

    [Fact]
    public async Task ConcurrentDifferentIdentitiesConsumeOneTicketAndPhysicalWriteFailureRollsBackConsumption()
    {
        var f = await Setup();
        f.Select();
        Sql(f.Store, "CREATE TRIGGER reject_physical BEFORE INSERT ON PlcTriggers BEGIN SELECT RAISE(ABORT,'fixture physical write'); END;");
        var first = f.Started(new InspectionIdentity(Guid.NewGuid(), 1));
        Assert.Throws<SqliteException>(() => f.Store.BeginAccepted(first, f.Images.Loaded.BundleHash));
        Assert.Null(f.Store.Get(first.Id));
        Assert.Null(f.Store.ReadSelectedReworkOrder()!.InspectionId);
        Assert.Null(f.Store.ReadUnacknowledgedPlc());
        Assert.Equal(1, f.Store.ReadActiveBatch()!.AcceptedProductionCount);
        Sql(f.Store, "DROP TRIGGER reject_physical;");
        var attempts = new[] { f.Started(new InspectionIdentity(Guid.NewGuid(), 1)), f.Started(new InspectionIdentity(Guid.NewGuid(), 1)) };
        var results = await Task.WhenAll(attempts.Select(record => Task.Run(() =>
        {
            try { return f.Store.BeginAccepted(record, f.Images.Loaded.BundleHash); }
            catch (InspectionRejectedException) { return null; }
        })));
        var winner = Assert.Single(results, result => result is not null)!;
        Assert.Equal(winner.Record.Id, f.Store.ReadSelectedReworkOrder()!.InspectionId);
        Assert.Equal(winner.Record.Id, f.Store.ReadUnacknowledgedPlc()!.Record.Id);
        Assert.Equal(3, f.Store.ReadRecent().Count);
        Assert.Equal(2, f.Store.PendingCount());
        Assert.Equal(1, f.Coordinator.RecoverInterrupted());
        Assert.Equal(3, f.Store.PendingCount());
    }

    [Fact]
    public async Task FailedResultCommitLeavesConsumedStartedForAtomicInterruptedRecovery()
    {
        var f = await Setup();
        f.Select();
        Sql(f.Store, "CREATE TRIGGER reject_result BEFORE INSERT ON UploadState BEGIN SELECT RAISE(ABORT,'fixture result commit'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => f.Coordinator.InspectAsync(InspectionPurpose.Reinspection,
            f.Batch.StationId, f.Order.ProductId, Actor, f.Source()));
        Assert.True(f.Coordinator.IsFaulted);
        var id = f.Store.ReadSelectedReworkOrder()!.InspectionId!.Value;
        Assert.Equal(InspectionExecution.Started, f.Store.Get(id)!.ExecutionStatus);
        Assert.Null(f.Store.Get(id)!.TestedImage);
        Assert.Empty(f.Store.ReadPendingReworkOrders(f.Batch.Id));
        Assert.Throws<SqliteException>(() => f.Store.RecoverInterrupted());
        Assert.Equal(InspectionExecution.Started, f.Store.Get(id)!.ExecutionStatus);
        Sql(f.Store, "DROP TRIGGER reject_result;");
        Assert.Equal(1, f.Store.RecoverInterrupted());
        Assert.Equal(InspectionExecution.Interrupted, f.Store.Get(id)!.ExecutionStatus);
        Assert.Equal(id, f.Store.ReadSelectedReworkOrder()!.InspectionId);
        Assert.Equal(InspectionTransfer.Hash(f.Original), InspectionTransfer.Hash(f.Store.Get(f.Original.Id)!));
        Assert.Equal(3, f.Store.PendingCount());
    }

    [Fact]
    public async Task CacheRefreshCannotResetConsumptionOrReplaceImmutableOrderAndWrongBatchRollsBackWholeResponse()
    {
        var f = await Setup();
        Assert.Throws<InspectionRejectedException>(() => f.Store.CacheReworkOrders(f.Batch.Id,
            [f.Order, f.Order with { Id = Guid.NewGuid(), StationId = "another" }]));
        Assert.Empty(f.Store.ReadPendingReworkOrders(f.Batch.Id));
        f.Select();
        var accepted = await f.Coordinator.InspectAsync(InspectionPurpose.Reinspection, f.Batch.StationId, f.Order.ProductId, Actor, f.Source());
        f.Store.CacheReworkOrders(f.Batch.Id, [f.Order]);
        Assert.Throws<InspectionRejectedException>(() => f.Store.CacheReworkOrders(f.Batch.Id, [f.Order with { Reason = "changed" }]));
        Assert.Equal(f.Order, f.Store.ReadSelectedReworkOrder()!.Order);
        Assert.Equal(accepted.Id, f.Store.ReadSelectedReworkOrder()!.InspectionId);
        Assert.Empty(f.Store.ReadPendingReworkOrders(f.Batch.Id));
        Assert.Throws<InspectionRejectedException>(() => f.Coordinator.UseReworkOrder(f.Order.Id));
    }

    private static void Sql(LocalInspectionStore store, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class CountingSource(IImageSource source) : IImageSource
    {
        public int Count { get; private set; }
        public string SampleId => source.SampleId;
        public string SourceKind => source.SourceKind;
        public Task<CapturedPair> CaptureAsync(CancellationToken token) { Count++; return source.CaptureAsync(token); }
    }
}
