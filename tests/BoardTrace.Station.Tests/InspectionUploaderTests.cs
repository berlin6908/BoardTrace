using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Tests;

public sealed class InspectionUploaderTests
{
    private static (LocalInspectionStore Store, InspectionRecord Record) Setup()
    {
        var store = new LocalInspectionStore(Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString(), "station.db"));
        store.Initialize();
        return (store, AddRecord(store));
    }

    private static InspectionRecord AddRecord(LocalInspectionStore store)
    {
        var record = new InspectionRecord
        {
            Id = Guid.NewGuid(), StationId = "TEST-UPLOAD", ProductId = "SIM-" + Guid.NewGuid(), SampleId = "sample-1", Purpose = InspectionPurpose.EngineeringReplay,
            OperatorId = "operator-1", OperatorName = "Operator One",
            SourceKind = "Replay", RecipeId = "test-recipe", RecipeJson = "{}", StartedAt = DateTimeOffset.UtcNow
        };
        store.BeginAccepted(record, null);
        record = record with
        {
            ExecutionStatus = InspectionExecution.Completed, Decision = QualityDecision.Fail,
            CompletedAt = DateTimeOffset.UtcNow, TestedImage = [1, 2, 3], ReferenceImage = [4, 5, 6],
            Defects = [new DefectBox([12, 20, 33, 45], null, 1, 25)]
        };
        store.Complete(record);
        return record;
    }

    private static HttpResponseMessage Receipt(InspectionRecord record, HttpStatusCode status = HttpStatusCode.Created) =>
        new(status) { Content = JsonContent.Create(new InspectionReceipt(record.Id, InspectionTransfer.Hash(record), DateTimeOffset.UtcNow)) };

    [Fact]
    public async Task DeviceSessionExpiryRelogsAndRetriesTheSameRecordWithOriginalOperator()
    {
        var (store, record) = Setup();
        var bodies = new List<string>();
        var logins = 0;
        using var client = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
                logins++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new CurrentUser("device-1", "station-test", "Device", ["Station"], record.StationId))
                };
            }
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return bodies.Count == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Receipt(record);
        })) { BaseAddress = new Uri("http://localhost/") };
        var result = await new InspectionUploader(store, client, new StationCredentials("station-test", "test-only-password")).UploadPendingAsync();
        Assert.Equal(1, logins);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal(1, result.Uploaded);
        Assert.Equal(0, result.Pending);
        Assert.Equal(record.OperatorId, store.Get(record.Id)!.OperatorId);
        Assert.Equal(record.OperatorName, store.Get(record.Id)!.OperatorName);
        Assert.NotNull(Assert.Single(store.ReadRecent()).AcknowledgedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidDeviceLoginOrWrongStationBindingCannotAcknowledge(bool wrongBinding)
    {
        var (store, record) = Setup();
        var puts = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                puts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }
            return Task.FromResult(wrongBinding ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CurrentUser("device-1", "station-test", "Device", ["Station"], "OTHER-STATION"))
            } : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        })) { BaseAddress = new Uri("http://localhost/") };
        var result = await new InspectionUploader(store, client, new StationCredentials("station-test", "test-only-password")).UploadPendingAsync();
        Assert.Equal(UploadConnection.Rejected, result.Connection);
        Assert.Equal(1, puts);
        Assert.Equal(1, result.Pending);
        Assert.Equal(0, result.Uploaded);
        Assert.DoesNotContain("test-only-password", result.Error);
        Assert.Null(Assert.Single(store.ReadRecent()).AcknowledgedAt);
    }

    [Fact]
    public async Task LostResponseRetainsImmutableRecordAndRetryAcknowledgesTheSameContent()
    {
        var (store, record) = Setup();
        var sentBodies = new List<string>();
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal($"/api/inspections/{record.Id:D}", request.RequestUri!.AbsolutePath);
            sentBodies.Add(await request.Content!.ReadAsStringAsync());
            if (sentBodies.Count == 1) throw new HttpRequestException("Server committed; response was lost.");
            return Receipt(record, HttpStatusCode.OK);
        })) { BaseAddress = new Uri("http://localhost/") };
        var uploader = new InspectionUploader(store, client, new StationCredentials("station-test", "test-only-password"));

        var lost = await uploader.UploadPendingAsync();
        Assert.Equal(UploadConnection.Unavailable, lost.Connection);
        Assert.Equal(0, lost.Uploaded);
        Assert.Equal(1, store.PendingCount());
        Assert.Null(Assert.Single(store.ReadRecent()).AcknowledgedAt);
        Assert.Equal(InspectionTransfer.Hash(record), InspectionTransfer.Hash(Assert.Single(store.ReadPending())));

        var retried = await uploader.UploadPendingAsync();
        Assert.Equal(1, retried.Uploaded);
        Assert.Equal(0, retried.Pending);
        Assert.Equal(sentBodies[0], sentBodies[1]);
        var reopened = new LocalInspectionStore(store.DatabasePath);
        var archived = Assert.Single(reopened.ReadRecent());
        Assert.False(archived.PendingUpload);
        Assert.NotNull(archived.AcknowledgedAt);
        Assert.Equal(record.TestedImage, reopened.Get(record.Id)!.TestedImage);
        Assert.Equal(record.ReferenceImage, reopened.Get(record.Id)!.ReferenceImage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceiptWithDifferentIdOrContentCannotAcknowledgeTheLocalRecord(bool wrongId)
    {
        var (store, record) = Setup();
        var receipt = new InspectionReceipt(wrongId ? Guid.NewGuid() : record.Id,
            wrongId ? InspectionTransfer.Hash(record) : new string('0', 64), DateTimeOffset.UtcNow);
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(receipt)
        }))) { BaseAddress = new Uri("http://localhost/") };

        var result = await new InspectionUploader(store, client, new StationCredentials("station-test", "test-only-password")).UploadPendingAsync();
        Assert.Equal(UploadConnection.Rejected, result.Connection);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, store.PendingCount());
        Assert.Null(Assert.Single(store.ReadRecent()).AcknowledgedAt);
    }

    [Theory]
    [InlineData(409, UploadConnection.Rejected)]
    [InlineData(503, UploadConnection.Unavailable)]
    [InlineData(202, UploadConnection.Rejected)]
    public async Task ConflictServerErrorAndUncommittedAcceptancePreservePending(int status, UploadConnection connection)
    {
        var (store, record) = Setup();
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Receipt(record, (HttpStatusCode)status))))
            { BaseAddress = new Uri("http://localhost/") };
        var result = await new InspectionUploader(store, client, new StationCredentials("station-test", "test-only-password")).UploadPendingAsync();
        Assert.Equal(connection, result.Connection);
        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, store.PendingCount());
        Assert.Null(Assert.Single(store.ReadRecent()).AcknowledgedAt);
    }

    [Fact]
    public async Task BatchLimitBoundsUploadsAndAcknowledgmentFailureLeavesTheOutboxIntact()
    {
        var (store, _) = Setup();
        AddRecord(store);
        AddRecord(store);
        using var client = new HttpClient(new Handler(async request =>
            Receipt((await request.Content!.ReadFromJsonAsync<InspectionRecord>())!))) { BaseAddress = new Uri("http://localhost/") };
        var result = await new InspectionUploader(store, client, new StationCredentials("station-test", "test-only-password")).UploadPendingAsync(2);
        Assert.Equal(2, result.Uploaded);
        Assert.Equal(1, result.Pending);

        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_receipt BEFORE INSERT ON UploadReceipts BEGIN SELECT RAISE(ABORT, 'simulated acknowledgment write failure'); END;";
        command.ExecuteNonQuery();
        await Assert.ThrowsAsync<SqliteException>(() => new InspectionUploader(store, client, new StationCredentials("station-test", "test-only-password")).UploadPendingAsync());
        Assert.Equal(1, store.PendingCount());
        Assert.Equal(2, store.ReadRecent().Count(row => row.AcknowledgedAt is not null));
        Assert.Single(store.ReadPending());
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
