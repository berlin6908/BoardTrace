using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Tests;

public sealed class LocalImageRetentionTests
{
    private static readonly CurrentUser Operator = new("retention-operator", "operator", "Retention Operator", ["Operator"], null);
    private static LocalInspectionStore CreateStore()
    {
        var store = new LocalInspectionStore(Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString(), "station.db"));
        store.Initialize(); return store;
    }
    private static InspectionRecord Started(InspectionIdentity? identity = null) => new()
    {
        Id = Guid.NewGuid(), StationId = "ST-1", ProductId = "RETENTION-" + Guid.NewGuid(), SampleId = "controlled-1",
        OperatorId = Operator.Id, OperatorName = Operator.DisplayName, Purpose = InspectionPurpose.EngineeringReplay,
        SourceKind = "Replay", RecipeId = "retention-fixture", RecipeJson = "{}", StartedAt = DateTimeOffset.UtcNow,
        ControllerSessionId = identity?.ControllerSessionId, TriggerSequence = identity?.TriggerSequence
    };
    private static InspectionRecord Complete(LocalInspectionStore store, InspectionIdentity? identity = null)
    {
        var started = store.BeginAccepted(Started(identity), null).Record;
        var record = started with { ExecutionStatus = InspectionExecution.Completed, Decision = QualityDecision.Fail,
            CompletedAt = DateTimeOffset.UtcNow, TestedImage = [1, 2, 3], ReferenceImage = [4, 5, 6],
            Defects = [new DefectBox([12, 20, 33, 45], null, 1, 25)] };
        store.Complete(record); return record;
    }
    private static DateTimeOffset Confirm(LocalInspectionStore store, InspectionRecord record)
    {
        // A delayed/lost response may refer to an old central receive time. Retention starts locally.
        store.ConfirmUploaded(new(record.Id, InspectionTransfer.Hash(record), DateTimeOffset.UtcNow.AddDays(-30)));
        return store.ReadRecent().Single(row => row.Record.Id == record.Id).AcknowledgedAt!.Value;
    }
    private static string Document(LocalInspectionStore store, Guid id)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT Document FROM Inspections WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString()); return (string)command.ExecuteScalar()!;
    }
    private static void Sql(LocalInspectionStore store, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    [Fact]
    public void SevenDaysStartsAtLocalConfirmationAndCleanupRetainsOriginalReceiptAndMetadata()
    {
        var store = CreateStore(); var record = Complete(store); var acknowledged = Confirm(store, record);
        var originalDocument = Document(store, record.Id);
        var originalReceipt = store.ReadArchive(record.Id)!.Receipt;
        Assert.Equal(0, store.PurgeAcknowledgedImages(acknowledged.AddDays(7).AddMilliseconds(-1)));
        Assert.Equal(record.TestedImage, store.Get(record.Id)!.TestedImage);
        Assert.Null(store.ReadArchive(record.Id)!.ImagesPurgedAt);

        var cleanupAt = acknowledged.AddDays(7);
        Assert.Equal(1, store.PurgeAcknowledgedImages(cleanupAt));
        Assert.Equal(0, store.PurgeAcknowledgedImages(cleanupAt));
        var reopened = new LocalInspectionStore(store.DatabasePath);
        var archive = reopened.ReadArchive(record.Id)!;
        Assert.Equal(cleanupAt, archive.ImagesPurgedAt);
        Assert.Equal(originalReceipt, archive.Receipt);
        Assert.Equal(InspectionTransfer.Hash(record), archive.Receipt!.ContentHash);
        Assert.Equal(originalDocument, Document(reopened, record.Id));
        Assert.Null(archive.Record.TestedImage); Assert.Null(archive.Record.ReferenceImage);
        Assert.Equal(record.Defects.Single().Box, archive.Record.Defects.Single().Box);
        Assert.Contains("已按保留期清理", Assert.Throws<InvalidOperationException>(() => reopened.Get(record.Id)).Message);
        Assert.Equal(acknowledged, Assert.Single(reopened.ReadRecent()).AcknowledgedAt);
        Assert.Empty(reopened.ReadPending()); Assert.Equal(0, reopened.PendingCount());
    }

    [Fact]
    public void PendingStartedAndUnacknowledgedPhysicalRecordsSurviveWhileEligibleHistoryIsPurged()
    {
        var store = CreateStore(); var eligible = Complete(store); var acknowledged = Confirm(store, eligible);
        var pending = Complete(store);
        var started = store.BeginAccepted(Started(), null).Record;
        var identity = new InspectionIdentity(Guid.NewGuid(), 7);
        var held = Complete(store, identity); Confirm(store, held);
        var now = acknowledged.AddDays(8);

        Assert.Equal(1, store.PurgeAcknowledgedImages(now));
        Assert.NotNull(store.ReadArchive(eligible.Id)!.ImagesPurgedAt);
        Assert.Equal(pending.TestedImage, store.Get(pending.Id)!.TestedImage);
        Assert.Equal(InspectionTransfer.Hash(pending), InspectionTransfer.Hash(Assert.Single(store.ReadPending())));
        Assert.Equal(InspectionExecution.Started, store.Get(started.Id)!.ExecutionStatus);
        Assert.Null(store.ReadArchive(started.Id)!.ImagesPurgedAt);
        Assert.Equal(held.TestedImage, store.Get(held.Id)!.TestedImage);
        Assert.Null(store.ReadArchive(held.Id)!.ImagesPurgedAt);
        Assert.Equal(held.Id, store.ReadUnacknowledgedPlc()!.Record.Id);

        store.ConfirmPlcAck(held.Id);
        Assert.Equal(1, store.PurgeAcknowledgedImages(now));
        Assert.Equal(1, store.PendingCount());
        Assert.NotNull(store.ReadPlcTrigger(identity)!.AckAt);
        Assert.Equal(held.Id, store.ReadPlcTrigger(identity)!.Record.Id);
    }

    [Fact]
    public void FailureAfterOneImageUpdateRollsBackEveryImageAndCleanupMarker()
    {
        var store = CreateStore(); var first = Complete(store); Confirm(store, first);
        var second = Complete(store); var acknowledged = Confirm(store, second);
        Sql(store, $"""
            CREATE TRIGGER fail_second_image_cleanup BEFORE UPDATE OF TestedImage ON Inspections
            WHEN NEW.TestedImage IS NULL AND
                (SELECT COUNT(*) FROM Inspections WHERE Id IN ('{first.Id}','{second.Id}') AND TestedImage IS NULL)=1
            BEGIN SELECT RAISE(ABORT,'fixture storage failure after first image update'); END;
            """);
        Assert.Throws<SqliteException>(() => store.PurgeAcknowledgedImages(acknowledged.AddDays(7)));
        foreach (var record in new[] { first, second })
        {
            Assert.Equal(InspectionTransfer.Hash(record), InspectionTransfer.Hash(store.Get(record.Id)!));
            Assert.Null(store.ReadArchive(record.Id)!.ImagesPurgedAt);
            Assert.Equal(InspectionTransfer.Hash(record), store.ReadArchive(record.Id)!.Receipt!.ContentHash);
        }
        Sql(store, "DROP TRIGGER fail_second_image_cleanup;");
        Assert.Equal(2, store.PurgeAcknowledgedImages(acknowledged.AddDays(7)));
    }

    [Fact]
    public async Task PurgedProductionIdentityRemainsDuplicateAndItsSelectedBatchAndReferenceStillRestore()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var batch = new BatchDefinition(Guid.NewGuid(), "RETENTION-BATCH", "PCB", "TOP", 2, "ST-1", f.Loaded.VersionId,
            f.Loaded.BundleHash, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        coordinator.UseBatch(new(batch, BatchStatus.AwaitingFirstArticle, null, f.Version), f.Loaded);
        var sample = new ReplaySample("controlled-1", "tested.png", "reference.png");
        var first = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId, "FIRST", Operator,
            new PublishedReplayImageSource(f.Folder, sample, f.Reference));
        Assert.Equal(QualityDecision.Pass, first.Decision);
        var approval = new FirstArticleApproval(batch.Id, first.Id, "quality", "Fixture Quality", DateTimeOffset.UtcNow);
        coordinator.UseBatch(new(batch, BatchStatus.Approved, approval, f.Version), f.Loaded);
        coordinator.StartBatch(new(Guid.NewGuid(), batch.Id, batch.StationId, batch.RecipeBundleHash, first.Id,
            Operator.Id, Operator.DisplayName, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)), null);
        var identity = new InspectionIdentity(Guid.NewGuid(), uint.MaxValue);
        var production = await coordinator.InspectAsync(InspectionPurpose.Production, batch.StationId, "PRODUCTION", Operator,
            new PublishedReplayImageSource(f.Folder, sample), identity: identity);
        Assert.Equal(QualityDecision.Fail, production.Decision);
        Confirm(f.Store, first); var acknowledged = Confirm(f.Store, production);
        coordinator.ConfirmPlcAck(production.Id);
        var active = f.Store.ReadActiveBatch(); var ack = f.Store.ReadPlcTrigger(identity)!.AckAt;

        Assert.Equal(2, f.Store.PurgeAcknowledgedImages(acknowledged.AddDays(7)));
        Assert.Equal(active, f.Store.ReadActiveBatch());
        Assert.Equal(first.Id, f.Store.ReadPassedFirstArticle(batch.Id)!.Record.Id);
        var loaded = new LocalRecipeStore(f.Store.DatabasePath).Load(batch.RecipeVersionId);
        Assert.Equal(f.Reference, loaded.GetReferenceBytes(sample.SampleId));
        var reopenedStore = new LocalInspectionStore(f.Store.DatabasePath);
        var reopened = new InspectionCoordinator(reopenedStore, new ClassicalSettings());
        reopened.RestoreCachedBatchRecipe(loaded);
        reopened.EndOperatorSession();
        var notified = false;
        var duplicate = await reopened.InspectAsync(InspectionPurpose.Production, batch.StationId, "PRODUCTION", Operator,
            new MustNotCapture(), identity: identity, startedCommitted: _ => notified = true);
        Assert.False(notified);
        Assert.Equal(production.Id, duplicate.Id); Assert.Equal(1, duplicate.ProductionSequence);
        Assert.Equal(production.Decision, duplicate.Decision);
        Assert.Equal(ack, reopenedStore.ReadPlcTrigger(identity)!.AckAt);
        Assert.Equal(2, reopenedStore.ReadActiveBatch()!.NextProductionSequence);
        Assert.Equal(2, reopenedStore.ReadRecent().Count);
        Assert.Empty(reopenedStore.ReadPending());
        Assert.Equal(InspectionTransfer.Hash(production), reopenedStore.ReadArchive(production.Id)!.Receipt!.ContentHash);
    }

    [Fact]
    public void BacklogIsPurgedInBatchesOfOneHundredWithoutDeletingMetadataOrPendingImages()
    {
        var store = CreateStore(); var confirmed = new List<InspectionRecord>();
        var acknowledged = DateTimeOffset.MinValue;
        for (var i = 0; i < 105; i++)
        {
            var record = Complete(store); acknowledged = Confirm(store, record); confirmed.Add(record);
        }
        var pending = Complete(store); var now = acknowledged.AddDays(7);
        Assert.Equal(100, store.PurgeAcknowledgedImages(now));
        Assert.Equal(100, confirmed.Count(record => store.ReadArchive(record.Id)!.ImagesPurgedAt is not null));
        Assert.Equal(InspectionTransfer.Hash(pending), InspectionTransfer.Hash(Assert.Single(store.ReadPending())));
        Assert.Equal(5, store.PurgeAcknowledgedImages(now));
        Assert.Equal(0, store.PurgeAcknowledgedImages(now));
        Assert.Equal(106, store.ReadRecent(200).Count);
        foreach (var record in confirmed)
        {
            var archive = store.ReadArchive(record.Id)!;
            Assert.NotNull(archive.ImagesPurgedAt);
            Assert.Equal(record.Id, archive.Record.Id);
            Assert.Equal(record.ProductId, archive.Record.ProductId);
            Assert.Equal(record.Decision, archive.Record.Decision);
            Assert.Equal(InspectionTransfer.Hash(record), archive.Receipt!.ContentHash);
        }
        Assert.Equal(1, store.PendingCount());
        Assert.Equal(pending.TestedImage, store.Get(pending.Id)!.TestedImage);
    }

    [Fact]
    public void PurgedArchiveCannotBeReadOrAcknowledgedAsAnUploadEvenIfErroneouslyRequeued()
    {
        var store = CreateStore(); var record = Complete(store); var acknowledged = Confirm(store, record);
        Assert.Equal(1, store.PurgeAcknowledgedImages(acknowledged.AddDays(7)));
        var receipt = store.ReadArchive(record.Id)!.Receipt!;
        Sql(store, $"INSERT INTO UploadState(InspectionId) VALUES('{record.Id}');");
        Assert.Throws<InvalidOperationException>(() => store.ReadPending());
        Assert.Throws<InvalidOperationException>(() => store.ConfirmUploaded(receipt));
        Assert.Equal(receipt, store.ReadArchive(record.Id)!.Receipt);
        Assert.Equal(1, store.PendingCount());
    }

    private sealed class MustNotCapture : IImageSource
    {
        public string SampleId => "controlled-1"; public string SourceKind => "Replay";
        public Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Historical identity must not recapture images.");
    }
}
