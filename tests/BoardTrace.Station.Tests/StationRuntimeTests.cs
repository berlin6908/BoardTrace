using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Tests;

public sealed class StationRuntimeTests
{
    private static readonly CurrentUser Actor = new("operator", "operator", "Fixture Operator", ["Operator"], null);

    [Fact]
    public async Task SnapshotDistinguishesAcceptedImageWorkUploadConfirmationAndPlcAckForEachPurpose()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        var batch = Batch(f);
        var empty = f.Store.ReadRuntime("ArchiveOnly");
        Assert.Null(empty.BatchId); Assert.Null(empty.ArchiveId);
        Assert.Equal(0, empty.PendingUploads);
        coordinator.UseBatch(new(batch, BatchStatus.AwaitingFirstArticle, null, f.Version), f.Loaded);
        var source = Source(f, true);
        var first = await PausedInspection(InspectionPurpose.FirstArticle, source, "FIRST", null, expectedFirst: 1, expectedProduction: 0, expectedReinspection: 0);
        Assert.Equal(QualityDecision.Pass, first.Decision);
        Confirm(f.Store, first);
        var approval = new FirstArticleApproval(batch.Id, first.Id, "quality", "Fixture Quality", DateTimeOffset.UtcNow);
        coordinator.UseBatch(new(batch, BatchStatus.Approved, approval, f.Version), f.Loaded);
        var archive = f.Store.ReadActiveBatch()!.ArchiveId;
        var session = new BatchExecutionSession(Guid.NewGuid(), batch.Id, batch.StationId, batch.RecipeBundleHash, first.Id,
            Actor.Id, Actor.DisplayName, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), archive);
        coordinator.StartBatch(session, [9]);
        var physical = new InspectionIdentity(Guid.NewGuid(), 1);
        var production = await PausedInspection(InspectionPurpose.Production, Source(f), "PRODUCT", physical, 1, 1, 0);
        Assert.Equal(QualityDecision.Fail, production.Decision);
        var waitingUpload = f.Store.ReadRuntime("WaitingAck");
        Assert.Equal(batch.Id, waitingUpload.BatchId); Assert.Equal(archive, waitingUpload.ArchiveId);
        Assert.Equal(1, waitingUpload.PendingUploads); Assert.False(waitingUpload.HasStartedInspection); Assert.True(waitingUpload.HasUnacknowledgedPlc);
        Confirm(f.Store, production);
        var waitingAck = f.Store.ReadRuntime("WaitingAck");
        Assert.Equal(0, waitingAck.PendingUploads); Assert.True(waitingAck.HasUnacknowledgedPlc);
        coordinator.ConfirmPlcAck(production.Id);
        Assert.False(f.Store.ReadRuntime("Ready").HasUnacknowledgedPlc);
        var order = new ReworkOrder(Guid.NewGuid(), production.Id, batch.Id, batch.StationId, production.ProductId, production.SampleId,
            batch.RecipeVersionId, batch.RecipeBundleHash, "Fixture", "quality", "Fixture Quality", DateTimeOffset.UtcNow);
        f.Store.CacheReworkOrders(batch.Id, [order]); coordinator.UseReworkOrder(order.Id);
        var reinspection = await PausedInspection(InspectionPurpose.Reinspection, Source(f), "PRODUCT", new(physical.ControllerSessionId, 2), 1, 1, 1);
        Confirm(f.Store, reinspection); coordinator.ConfirmPlcAck(reinspection.Id);
        var end = f.Store.ReadRuntime("AwaitingClosure");
        Assert.Equal((1, 1, 1, 0, false, false), (end.FirstArticleCount, end.ProductionCount, end.ReinspectionCount,
            end.PendingUploads, end.HasStartedInspection, end.HasUnacknowledgedPlc));
        Assert.Equal(archive, end.ArchiveId);
        Assert.Equal(1, f.Store.ReadActiveBatch()!.AcceptedProductionCount);

        async Task<InspectionRecord> PausedInspection(InspectionPurpose purpose, IImageSource input, string product,
            InspectionIdentity? identity, int expectedFirst, int expectedProduction, int expectedReinspection)
        {
            var paused = new PausedSource(input);
            var task = coordinator.InspectAsync(purpose, batch.StationId, product, Actor, paused, identity: identity);
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                var started = f.Store.ReadRuntime("Inspecting");
                Assert.Equal((expectedFirst, expectedProduction, expectedReinspection),
                    (started.FirstArticleCount, started.ProductionCount, started.ReinspectionCount));
                Assert.True(started.HasStartedInspection); Assert.Equal(0, started.PendingUploads);
                Assert.Equal(identity is not null, started.HasUnacknowledgedPlc);
            }
            finally { paused.Release.SetResult(); }
            return await task;
        }
    }

    [Fact]
    public async Task ClosedRefreshEndsSessionAllowsNextBatchAndPreservesOldArchivesWithoutReopening()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        var batch = Batch(f);
        Assert.Throws<InspectionRejectedException>(() => coordinator.UseBatch(new(batch, BatchStatus.Closed, null, f.Version), f.Loaded));
        Assert.Null(f.Store.ReadActiveBatch());
        coordinator.UseBatch(new(batch, BatchStatus.AwaitingFirstArticle, null, f.Version), f.Loaded);
        var first = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId, "FIRST", Actor, Source(f, true));
        var hash = InspectionTransfer.Hash(first);
        var approval = new FirstArticleApproval(batch.Id, first.Id, "quality", "Fixture Quality", DateTimeOffset.UtcNow);
        coordinator.UseBatch(new(batch, BatchStatus.Approved, approval, f.Version), f.Loaded);
        var archive = f.Store.ReadActiveBatch()!.ArchiveId;
        var session = new BatchExecutionSession(Guid.NewGuid(), batch.Id, batch.StationId, batch.RecipeBundleHash, first.Id,
            Actor.Id, Actor.DisplayName, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), archive);
        coordinator.StartBatch(session, [9]);
        var next = batch with { Id = Guid.NewGuid(), BatchNumber = "NEXT" };
        Assert.Throws<InspectionRejectedException>(() => coordinator.UseBatch(new(next, BatchStatus.AwaitingFirstArticle, null, f.Version), f.Loaded));
        // Fixture supplies a central Closed response; backend closure policy has independent SQL tests.
        coordinator.UseBatch(new(batch, BatchStatus.Closed, approval, f.Version), f.Loaded);
        Assert.Equal(BatchStatus.Closed, f.Store.ReadActiveBatch()!.Status);
        Assert.Null(f.Store.ReadActiveBatch()!.Session); Assert.Null(f.Store.ReadBatchResume());
        Assert.Equal(archive, f.Store.ReadRuntime("Closed").ArchiveId);
        coordinator.UseBatch(new(batch, BatchStatus.Approved, approval, f.Version), f.Loaded);
        Assert.Equal(BatchStatus.Closed, f.Store.ReadActiveBatch()!.Status);
        Assert.Throws<InspectionRejectedException>(() => coordinator.StartBatch(session, [9]));
        coordinator.UseBatch(new(next, BatchStatus.AwaitingFirstArticle, null, f.Version), f.Loaded);
        var runtime = f.Store.ReadRuntime("AwaitingFirstArticle");
        Assert.Equal(next.Id, runtime.BatchId); Assert.NotEqual(archive, runtime.ArchiveId);
        Assert.Equal((0, 0, 0), (runtime.FirstArticleCount, runtime.ProductionCount, runtime.ReinspectionCount));
        // Whole-station backlog remains visible even when it belongs to the previous batch.
        Assert.Equal(1, runtime.PendingUploads);
        Assert.Equal(hash, InspectionTransfer.Hash(f.Store.Get(first.Id)!));
        Assert.Equal(1, f.Store.ReadActiveBatch()!.NextProductionSequence);
        Confirm(f.Store, first);
        Assert.Equal(0, f.Store.ReadRuntime("AwaitingFirstArticle").PendingUploads);
    }

    [Fact]
    public async Task FailedResultTransactionRemainsStartedInRuntimeUntilInterruptedRecoveryCommits()
    {
        var f = PublishedInspectionCoordinatorTests.Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        var batch = Batch(f);
        coordinator.UseBatch(new(batch, BatchStatus.AwaitingFirstArticle, null, f.Version), f.Loaded);
        Sql(f.Store, "CREATE TRIGGER reject_outbox BEFORE INSERT ON UploadState BEGIN SELECT RAISE(ABORT,'fixture'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId,
            "FAULT", Actor, Source(f, true), identity: new InspectionIdentity(Guid.NewGuid(), 1)));
        var fault = f.Store.ReadRuntime("Faulted", "Fixture result commit failed");
        Assert.Equal((1, 0, true, true), (fault.FirstArticleCount, fault.PendingUploads, fault.HasStartedInspection, fault.HasUnacknowledgedPlc));
        Assert.Throws<SqliteException>(() => f.Store.RecoverInterrupted());
        Assert.True(f.Store.ReadRuntime("Faulted").HasStartedInspection);
        Sql(f.Store, "DROP TRIGGER reject_outbox;");
        Assert.Equal(1, f.Store.RecoverInterrupted());
        var recovered = f.Store.ReadRuntime("ArchiveOnly");
        Assert.Equal((1, 1, false, true), (recovered.FirstArticleCount, recovered.PendingUploads, recovered.HasStartedInspection, recovered.HasUnacknowledgedPlc));
    }

    [Fact]
    public async Task RuntimeUsesDeviceLoginAfter401AndResendsExactlyTheSameSnapshot()
    {
        var report = new StationRuntimeUpdate(Guid.NewGuid(), Guid.NewGuid(), 1, 2, 3, 4, true, true, "Inspecting", null, DateTimeOffset.UtcNow);
        var calls = new List<string>();
        var bodies = new List<string>();
        var received = DateTimeOffset.UtcNow.AddSeconds(1);
        using var http = Client(async (request, _) =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
                return Json(new CurrentUser("station", "station", "Fixture Station", ["Station"], "ST-1"));
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1 ? new(HttpStatusCode.Unauthorized) : Json(new StationRuntimeReceipt("ST-1", received));
        });
        var result = await new StationRuntimeClient(http, new("station", "local fixture password"), "ST-1").ReportAsync(report);
        Assert.Equal(new[] { "PUT /api/stations/ST-1/runtime", "POST /api/auth/login", "PUT /api/stations/ST-1/runtime" }, calls);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal(received, result.ReceivedAt);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("wrong-station")]
    [InlineData("bad-device")]
    public async Task RejectedOrMismatchedRuntimeNeverReturnsASuccessReceipt(string failure)
    {
        var report = new StationRuntimeUpdate(null, null, 0, 0, 0, 0, false, false, "ArchiveOnly", null, DateTimeOffset.UtcNow);
        using var http = Client((request, _) => Task.FromResult(failure switch
        {
            "unavailable" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "wrong-station" => Json(new StationRuntimeReceipt("OTHER", DateTimeOffset.UtcNow)),
            _ when request.Method == HttpMethod.Put => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => Json(new CurrentUser("human", "human", "Human Operator", ["Operator"], null))
        }));
        var client = new StationRuntimeClient(http, new("station", "fixture"), "ST-1");
        if (failure == "unavailable") Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await Assert.ThrowsAsync<HttpRequestException>(() => client.ReportAsync(report))).StatusCode);
        else if (failure == "wrong-station") await Assert.ThrowsAsync<InvalidDataException>(() => client.ReportAsync(report));
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ReportAsync(report));
    }

    [Fact]
    public async Task CancellationStopsRuntimeRequestWithoutReloginOrFalseReceipt()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = Client(async (_, token) =>
        {
            calls++; cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Canceled request must not complete.");
        });
        var report = new StationRuntimeUpdate(null, null, 0, 0, 0, 0, false, false, "ArchiveOnly", null, DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StationRuntimeClient(http, new("station", "fixture"), "ST-1")
            .ReportAsync(report, cancellation.Token));
        Assert.Equal(1, calls);
    }

    private static BatchDefinition Batch(PublishedInspectionCoordinatorTests.Fixture f) => new(Guid.NewGuid(), "RUNTIME-FIXTURE", "PCB", "TOP", 1,
        "ST-1", f.Loaded.VersionId, f.Loaded.BundleHash, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
    private static IImageSource Source(PublishedInspectionCoordinatorTests.Fixture f, bool normal = false) =>
        new PublishedReplayImageSource(f.Folder, new ReplaySample("controlled-1", "tested.png", "reference.png"), normal ? f.Reference : null);
    private static void Confirm(LocalInspectionStore store, InspectionRecord record) =>
        store.ConfirmUploaded(new(record.Id, InspectionTransfer.Hash(record), DateTimeOffset.UtcNow));
    private static void Sql(LocalInspectionStore store, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new Handler(respond)) { BaseAddress = new("http://fixture.local/") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private sealed class PausedSource(IImageSource source) : IImageSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SampleId => source.SampleId;
        public string SourceKind => source.SourceKind;
        public async Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken)
        { Entered.SetResult(); await Release.Task.WaitAsync(cancellationToken); return await source.CaptureAsync(cancellationToken); }
    }
}
