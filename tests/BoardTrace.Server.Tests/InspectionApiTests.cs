using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Server.Tests;

public sealed class InspectionApiTests
{
    private static InspectionRecord Completed(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), StationId = "STATION-A", ProductId = "SIM-100", SampleId = "sample-100",
        SourceKind = "Replay", RecipeId = "classical-test", RecipeJson = "{}",
        StartedAt = new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 9, 13, 10, 0, 1, TimeSpan.Zero),
        ExecutionStatus = InspectionExecution.Completed, Decision = QualityDecision.Fail,
        Width = 640, Height = 640, DetectionMs = 12.5,
        Defects = [new DefectBox([10, 20, 40, 50], null, 1, 900)],
        TestedImage = [1, 2, 3, 4], ReferenceImage = [5, 6, 7, 8]
    };

    [Fact]
    public async Task FirstUploadAtomicallyExposesResultDefectsImagesAndFilteredQuery()
    {
        await using var server = await TestServer.CreateAsync();
        var record = Completed();
        using var response = await server.Client.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<InspectionReceipt>();
        Assert.NotNull(receipt);
        Assert.Equal(record.Id, receipt.InspectionId);
        Assert.Equal(InspectionTransfer.Hash(record), receipt.ContentHash);
        Assert.NotEqual(default, receipt.ReceivedAt);

        using var detail = await server.Client.GetAsync($"/api/inspections/{record.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var inspection = root.GetProperty("inspection");
        Assert.Equal(record.Id.ToString(), inspection.GetProperty("id").GetString());
        Assert.Equal("Fail", inspection.GetProperty("decision").GetString());
        Assert.Equal(1, inspection.GetProperty("defects").GetArrayLength());
        Assert.True(root.GetProperty("hasTestedImage").GetBoolean());
        Assert.True(root.GetProperty("hasReferenceImage").GetBoolean());
        Assert.Equal(receipt.ContentHash, root.GetProperty("contentHash").GetString());
        Assert.Equal(JsonValueKind.Null, inspection.GetProperty("testedImage").ValueKind);
        Assert.Equal(JsonValueKind.Null, inspection.GetProperty("referenceImage").ValueKind);

        Assert.Equal(record.TestedImage, await server.Client.GetByteArrayAsync($"/api/inspections/{record.Id}/images/tested"));
        Assert.Equal(record.ReferenceImage, await server.Client.GetByteArrayAsync($"/api/inspections/{record.Id}/images/reference"));
        using var list = await server.Client.GetAsync("/api/inspections?stationId=STATION-A&productId=SIM-100&decision=Fail&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listing = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(1, listing.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(record.Id.ToString(), listing.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal((1, 1, 2), await server.CountsAsync());
    }

    [Fact]
    public async Task ConcurrentIdenticalUploadsReturnOneDurableInspection()
    {
        await using var server = await TestServer.CreateAsync();
        var record = Completed();
        var uploads = Enumerable.Range(0, 4)
            .Select(_ => server.Client.PutAsJsonAsync($"/api/inspections/{record.Id}", record)).ToArray();
        using var responses = new ResponseSet(await Task.WhenAll(uploads));
        Assert.Single(responses.Values, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(3, responses.Values.Count(r => r.StatusCode == HttpStatusCode.OK));
        var receipts = await Task.WhenAll(responses.Values.Select(r => r.Content.ReadFromJsonAsync<InspectionReceipt>()));
        Assert.All(receipts, receipt => Assert.Equal(InspectionTransfer.Hash(record), receipt!.ContentHash));
        Assert.Equal((1, 1, 2), await server.CountsAsync());
    }

    [Fact]
    public async Task SameIdWithDifferentPayloadConflictsWithoutReplacingOriginal()
    {
        await using var server = await TestServer.CreateAsync();
        var record = Completed();
        using var first = await server.Client.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var conflict = await server.Client.PutAsJsonAsync($"/api/inspections/{record.Id}", record with { ProductId = "SIM-OTHER", TestedImage = [9] });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(record.TestedImage, await server.Client.GetByteArrayAsync($"/api/inspections/{record.Id}/images/tested"));
        Assert.Equal((1, 1, 2), await server.CountsAsync());
    }

    [Fact]
    public async Task ImageInsertFailureRollsBackHeaderDefectsAndReceipt()
    {
        await using var server = await TestServer.CreateAsync();
        await server.ExecuteAsync("CREATE TRIGGER dbo.RejectTestImage ON dbo.InspectionImages AFTER INSERT AS BEGIN THROW 51000, 'simulated image write failure', 1; END");
        var record = Completed();
        using var response = await server.Client.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal((0, 0, 0), await server.CountsAsync());
        using var detail = await server.Client.GetAsync($"/api/inspections/{record.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task FailedExecutionRemainsNotEvaluatedWhenUploaded()
    {
        await using var server = await TestServer.CreateAsync();
        var record = Completed() with
        {
            ExecutionStatus = InspectionExecution.Failed, Decision = QualityDecision.NotEvaluated,
            Defects = [], DetectionMs = null, Error = "Corrupt image", TestedImage = [1, 2, 3]
        };
        using var response = await server.Client.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var detail = await server.Client.GetAsync($"/api/inspections/{record.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal("Failed", json.RootElement.GetProperty("inspection").GetProperty("executionStatus").GetString());
        Assert.Equal("NotEvaluated", json.RootElement.GetProperty("inspection").GetProperty("decision").GetString());
        Assert.Equal((1, 0, 2), await server.CountsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidResultCannotCreateAnySqlArchive(bool failedWithPass)
    {
        await using var server = await TestServer.CreateAsync();
        var record = failedWithPass
            ? Completed() with { ExecutionStatus = InspectionExecution.Failed, Decision = QualityDecision.Pass }
            : Completed() with { TestedImage = null };
        using var response = await server.Client.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((0, 0, 0), await server.CountsAsync());
    }

    [Fact]
    public async Task CommittedCentralUploadWithLostResponseIsRetriedAndAcknowledgedLocally()
    {
        await using var server = await TestServer.CreateAsync();
        var sqlitePath = Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString("N"), "station.db");
        var store = new LocalInspectionStore(sqlitePath);
        store.Initialize();
        var started = Completed() with
        {
            ExecutionStatus = InspectionExecution.Started, Decision = QualityDecision.NotEvaluated,
            CompletedAt = null, Defects = [], TestedImage = null, ReferenceImage = null
        };
        var record = Completed(started.Id);
        store.Begin(started);
        store.Complete(record);

        using var client = server.CreateClientWithLostFirstResponse();
        var uploader = new InspectionUploader(store, client);
        var first = await uploader.UploadPendingAsync();
        Assert.Equal(UploadConnection.Unavailable, first.Connection);
        Assert.Equal(0, first.Uploaded);
        Assert.Equal(1, store.PendingCount());
        Assert.Null(Assert.Single(store.ReadRecent()).AcknowledgedAt);
        Assert.Equal((1, 1, 2), await server.CountsAsync());
        Assert.Equal(record.TestedImage, await server.Client.GetByteArrayAsync($"/api/inspections/{record.Id}/images/tested"));
        Assert.Equal(record.ReferenceImage, await server.Client.GetByteArrayAsync($"/api/inspections/{record.Id}/images/reference"));

        var second = await uploader.UploadPendingAsync();
        Assert.Equal(UploadConnection.Connected, second.Connection);
        Assert.Equal(1, second.Uploaded);
        Assert.Equal(0, second.Pending);
        var reopened = new LocalInspectionStore(sqlitePath);
        Assert.Equal(0, reopened.PendingCount());
        var archive = Assert.Single(reopened.ReadRecent());
        Assert.False(archive.PendingUpload);
        Assert.NotNull(archive.AcknowledgedAt);
        Assert.Equal(record.TestedImage, reopened.Get(record.Id)!.TestedImage);
        Assert.Equal(record.ReferenceImage, reopened.Get(record.Id)!.ReferenceImage);
        using (var sqlite = new SqliteConnection($"Data Source={sqlitePath}"))
        {
            sqlite.Open();
            using var command = sqlite.CreateCommand();
            command.CommandText = "SELECT ContentHash FROM UploadReceipts WHERE InspectionId=$id";
            command.Parameters.AddWithValue("$id", record.Id.ToString());
            Assert.Equal(InspectionTransfer.Hash(record), command.ExecuteScalar());
        }
        Assert.Equal((1, 1, 2), await server.CountsAsync());
    }

    private sealed class ResponseSet(HttpResponseMessage[] values) : IDisposable
    {
        public HttpResponseMessage[] Values => values;
        public void Dispose() { foreach (var value in values) value.Dispose(); }
    }

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly string name = "BoardTrace_Integration_" + Guid.NewGuid().ToString("N");
        private WebApplicationFactory<Program>? factory;
        public HttpClient Client { get; private set; } = null!;
        public HttpClient CreateClientWithLostFirstResponse() => new(new LoseFirstResponseHandler
        {
            InnerHandler = factory!.Server.CreateHandler()
        }) { BaseAddress = Client.BaseAddress };
        private string ConnectionString => $"Server=(localdb)\\BoardTrace;Database={name};Integrated Security=true;TrustServerCertificate=true";
        private static string MasterConnection => "Server=(localdb)\\BoardTrace;Database=master;Integrated Security=true;TrustServerCertificate=true";

        public static async Task<TestServer> CreateAsync()
        {
            var server = new TestServer();
            await using (var master = new SqlConnection(MasterConnection))
            {
                await master.OpenAsync();
                await using var create = master.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{server.name}]";
                await create.ExecuteNonQueryAsync();
            }
            try
            {
                server.factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["ConnectionStrings:BoardTrace"] = server.ConnectionString })));
                server.Client = server.factory.CreateClient();
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<(int Inspections, int Defects, int Images)> CountsAsync()
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            async Task<int> CountAsync(string table)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM dbo.[{table}]";
                return Convert.ToInt32(await command.ExecuteScalarAsync());
            }
            return (await CountAsync("Inspections"), await CountAsync("Defects"), await CountAsync("InspectionImages"));
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            factory?.Dispose();
            // This instance can only remove the unique database name it generated itself.
            if (!name.StartsWith("BoardTrace_Integration_", StringComparison.Ordinal) ||
                name.Length != "BoardTrace_Integration_".Length + 32 ||
                !name.AsSpan("BoardTrace_Integration_".Length).ToString().All(Uri.IsHexDigit))
                throw new InvalidOperationException("Unexpected test database name; refusing cleanup.");
            await using var master = new SqlConnection(MasterConnection);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class LoseFirstResponseHandler : DelegatingHandler
    {
        private bool first = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (first)
            {
                first = false;
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                response.Dispose();
                throw new HttpRequestException("Central SQL transaction committed; HTTP response lost.");
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response;
        }
    }
}
