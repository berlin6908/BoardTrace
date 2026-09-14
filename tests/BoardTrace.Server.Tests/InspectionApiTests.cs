using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Station.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Server.Tests;

public sealed class InspectionApiTests
{
    private static InspectionRecord Completed(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), StationId = "STATION-A", ProductId = "SIM-100", Purpose = InspectionPurpose.EngineeringReplay,
        OperatorId = "operator-test", OperatorName = "Test Operator", SampleId = "sample-100",
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

        using var detail = await server.HumanClient.GetAsync($"/api/inspections/{record.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var inspection = root.GetProperty("inspection");
        Assert.Equal(record.Id.ToString(), inspection.GetProperty("id").GetString());
        Assert.Equal("Fail", inspection.GetProperty("decision").GetString());
        Assert.Equal(record.OperatorId, inspection.GetProperty("operatorId").GetString());
        Assert.Equal(record.OperatorName, inspection.GetProperty("operatorName").GetString());
        Assert.Equal(1, inspection.GetProperty("defects").GetArrayLength());
        Assert.True(root.GetProperty("hasTestedImage").GetBoolean());
        Assert.True(root.GetProperty("hasReferenceImage").GetBoolean());
        Assert.Equal(receipt.ContentHash, root.GetProperty("contentHash").GetString());
        Assert.Equal(JsonValueKind.Null, inspection.GetProperty("testedImage").ValueKind);
        Assert.Equal(JsonValueKind.Null, inspection.GetProperty("referenceImage").ValueKind);

        Assert.Equal(record.TestedImage, await server.HumanClient.GetByteArrayAsync($"/api/inspections/{record.Id}/images/tested"));
        using (var protectedImage = await server.HumanClient.GetAsync($"/api/inspections/{record.Id}/images/tested"))
            Assert.Contains("no-store", protectedImage.Headers.CacheControl?.ToString() ?? "");
        Assert.Equal(record.ReferenceImage, await server.HumanClient.GetByteArrayAsync($"/api/inspections/{record.Id}/images/reference"));
        using var list = await server.HumanClient.GetAsync("/api/inspections?stationId=STATION-A&productId=SIM-100&decision=Fail&page=1&pageSize=10");
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
        Assert.Equal(record.TestedImage, await server.HumanClient.GetByteArrayAsync($"/api/inspections/{record.Id}/images/tested"));
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
        using var detail = await server.HumanClient.GetAsync($"/api/inspections/{record.Id}");
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
        using var detail = await server.HumanClient.GetAsync($"/api/inspections/{record.Id}");
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
    public async Task AnonymousAndWrongRoleCannotReadOrUpload()
    {
        await using var server = await TestServer.CreateAsync();
        using var anonymous = server.CreateClient();
        using var read = await anonymous.GetAsync("/api/inspections");
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        using var upload = await anonymous.PutAsJsonAsync($"/api/inspections/{Guid.NewGuid()}", Completed());
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        using var humanUpload = await server.HumanClient.PutAsJsonAsync($"/api/inspections/{Guid.NewGuid()}", Completed());
        Assert.Equal(HttpStatusCode.Forbidden, humanUpload.StatusCode);
        using var stationRead = await server.Client.GetAsync("/api/inspections");
        Assert.Equal(HttpStatusCode.Forbidden, stationRead.StatusCode);
        Assert.Equal((0, 0, 0), await server.CountsAsync());
    }

    [Fact]
    public async Task StationCannotUploadAnotherStationsRecordOrUnknownOperator()
    {
        await using var server = await TestServer.CreateAsync();
        var foreign = Completed() with { StationId = "STATION-B" };
        using var denied = await server.Client.PutAsJsonAsync($"/api/inspections/{foreign.Id}", foreign);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var unknown = Completed() with { OperatorId = "not-a-user" };
        using var invalid = await server.Client.PutAsJsonAsync($"/api/inspections/{unknown.Id}", unknown);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal((0, 0, 0), await server.CountsAsync());
    }

    [Fact]
    public async Task WrongPasswordFailsAndLogoutRevokesSessionCookie()
    {
        await using var server = await TestServer.CreateAsync();
        using var client = server.CreateClient();
        using var wrong = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("operator", "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var empty = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("", ""));
        Assert.Equal(HttpStatusCode.Unauthorized, empty.StatusCode);
        using var before = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        await TestServer.LoginAsync(client, "operator", "Test!Operator1");
        using var meResponse = await client.GetAsync("/api/auth/me");
        Assert.Contains("no-store", meResponse.Headers.CacheControl?.ToString() ?? "");
        var me = await meResponse.Content.ReadFromJsonAsync<CurrentUser>();
        Assert.NotNull(me);
        Assert.Equal("operator-test", me.Id);
        Assert.Contains("Operator", me.Roles);
        using var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var after = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
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
        store.BeginAccepted(started, null);
        store.Complete(record);

        using var client = await server.CreateClientWithLostFirstResponseAsync();
        var uploader = new InspectionUploader(store, client, new StationCredentials("station-a", "Test!Station1"));
        var first = await uploader.UploadPendingAsync();
        Assert.Equal(UploadConnection.Unavailable, first.Connection);
        Assert.Equal(0, first.Uploaded);
        Assert.Equal(1, store.PendingCount());
        Assert.Null(Assert.Single(store.ReadRecent()).AcknowledgedAt);
        Assert.Equal((1, 1, 2), await server.CountsAsync());
        Assert.Equal(record.TestedImage, await server.HumanClient.GetByteArrayAsync($"/api/inspections/{record.Id}/images/tested"));
        Assert.Equal(record.ReferenceImage, await server.HumanClient.GetByteArrayAsync($"/api/inspections/{record.Id}/images/reference"));

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
        public HttpClient HumanClient { get; private set; } = null!;
        public HttpClient CreateClient() => factory!.CreateClient();
        public async Task<HttpClient> CreateClientWithLostFirstResponseAsync()
        {
            var client = new HttpClient(new LoseFirstResponseHandler { InnerHandler = factory!.Server.CreateHandler() })
                { BaseAddress = Client.BaseAddress };
            await LoginAsync(client, "station-a", "Test!Station1");
            return client;
        }
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
                await server.SeedAsync();
                await LoginAsync(server.Client, "station-a", "Test!Station1");
                server.HumanClient = server.factory.CreateClient();
                await LoginAsync(server.HumanClient, "operator", "Test!Operator1");
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        private async Task SeedAsync()
        {
            using var scope = factory!.Services.CreateScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<BoardTraceUser>>();
            foreach (var role in new[] { "Operator", "QualityEngineer", "Station" })
                Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
            foreach (var (id, name, role, station, password) in new[]
            {
                ("operator-test", "operator", "Operator", (string?)null, "Test!Operator1"),
                ("quality-test", "quality", "QualityEngineer", (string?)null, "Test!Quality1"),
                ("station-a-test", "station-a", "Station", "STATION-A", "Test!Station1"),
                ("station-b-test", "station-b", "Station", "STATION-B", "Test!Station2")
            })
            {
                var user = new BoardTraceUser { Id = id, UserName = name, DisplayName = name, StationId = station };
                Assert.True((await users.CreateAsync(user, password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            }
        }

        public static async Task LoginAsync(HttpClient client, string userName, string password)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
            HumanClient?.Dispose();
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
        private string? cookie;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                cookie = cookies.First().Split(';')[0];
            if (first && request.Method == HttpMethod.Put)
            {
                first = false;
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                response.Dispose();
                throw new HttpRequestException("Central SQL transaction committed; HTTP response lost.");
            }
            if (request.Method == HttpMethod.Put) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response;
        }
    }
}
