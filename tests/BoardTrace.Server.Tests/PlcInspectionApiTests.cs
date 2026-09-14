using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BoardTrace.Server.Tests;

public sealed class PlcInspectionApiTests
{
    [Fact]
    public async Task PhysicalTriggerIdentityPersistsAndRetriesByInspectionId()
    {
        await using var server = await PlcServer.CreateAsync();
        var record = Record(Guid.NewGuid(), "STATION-A", Guid.NewGuid(), uint.MaxValue);

        using var first = await server.StationA.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var receipt = (await first.Content.ReadFromJsonAsync<InspectionReceipt>())!;
        Assert.Equal(InspectionTransfer.Hash(record), receipt.ContentHash);
        using var repeat = await server.StationA.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(receipt, await repeat.Content.ReadFromJsonAsync<InspectionReceipt>());

        var detail = await server.Human.GetFromJsonAsync<PlcDetail>($"/api/inspections/{record.Id}");
        Assert.NotNull(detail);
        Assert.Equal(record.ControllerSessionId, detail.Inspection.ControllerSessionId);
        Assert.Equal(record.TriggerSequence, detail.Inspection.TriggerSequence);
        Assert.Equal(receipt.ContentHash, detail.ContentHash);
        Assert.Equal(1, await server.CountAsync());
    }

    [Fact]
    public async Task ConcurrentDifferentIdsForSamePhysicalTriggerCommitOnlyOne()
    {
        await using var server = await PlcServer.CreateAsync();
        var session = Guid.NewGuid();
        var firstRecord = Record(Guid.NewGuid(), "STATION-A", session, 7);
        var secondRecord = firstRecord with { Id = Guid.NewGuid() };
        var first = server.StationA.PutAsJsonAsync($"/api/inspections/{firstRecord.Id}", firstRecord);
        var second = server.StationA.PutAsJsonAsync($"/api/inspections/{secondRecord.Id}", secondRecord);
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict],
            new[] { firstResponse.StatusCode, secondResponse.StatusCode }.Order().ToArray());
        Assert.Equal(1, await server.CountAsync());
        using var retry = await server.StationA.PutAsJsonAsync($"/api/inspections/{firstRecord.Id}", firstRecord);
        Assert.Equal(firstResponse.StatusCode == HttpStatusCode.Created ? HttpStatusCode.OK : HttpStatusCode.Conflict, retry.StatusCode);
    }

    [Fact]
    public async Task SameSequenceInAnotherStationOrSessionIsIndependent()
    {
        await using var server = await PlcServer.CreateAsync();
        var session = Guid.NewGuid();
        var records = new[]
        {
            Record(Guid.NewGuid(), "STATION-A", session, 4),
            Record(Guid.NewGuid(), "STATION-A", Guid.NewGuid(), 4),
            Record(Guid.NewGuid(), "STATION-B", session, 4)
        };
        foreach (var record in records)
        {
            var client = record.StationId == "STATION-A" ? server.StationA : server.StationB;
            using var response = await client.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        Assert.Equal(3, await server.CountAsync());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IncompleteOrZeroPhysicalIdentityDoesNotReachSql(bool hasSession, bool hasSequence)
    {
        await using var server = await PlcServer.CreateAsync();
        var record = Record(Guid.NewGuid(), "STATION-A", hasSession ? Guid.Empty : null, hasSequence ? 0u : null);
        using var response = await server.StationA.PutAsJsonAsync($"/api/inspections/{record.Id}", record);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await server.CountAsync());
    }

    private static InspectionRecord Record(Guid id, string station, Guid? session, uint? sequence) => new()
    {
        Id = id, StationId = station, ProductId = "SIM-100", Purpose = InspectionPurpose.EngineeringReplay,
        ControllerSessionId = session, TriggerSequence = sequence,
        OperatorId = "operator-plc-test", OperatorName = "Test Operator", SampleId = "sample-100",
        SourceKind = "PLC", RecipeId = "classical-test", RecipeJson = "{}",
        StartedAt = new DateTimeOffset(2026, 9, 14, 1, 0, 0, TimeSpan.Zero),
        CompletedAt = new DateTimeOffset(2026, 9, 14, 1, 0, 1, TimeSpan.Zero),
        ExecutionStatus = InspectionExecution.Completed, Decision = QualityDecision.Pass,
        Width = 640, Height = 640, TestedImage = [1, 2], ReferenceImage = [3, 4]
    };

    private sealed record PlcDetail(InspectionRecord Inspection, DateTimeOffset ReceivedAt, string ContentHash,
        bool HasTestedImage, bool HasReferenceImage);

    private sealed class PlcServer : IAsyncDisposable
    {
        private readonly string name = "BoardTrace_Integration_" + Guid.NewGuid().ToString("N");
        private WebApplicationFactory<Program>? factory;
        public HttpClient StationA { get; private set; } = null!;
        public HttpClient StationB { get; private set; } = null!;
        public HttpClient Human { get; private set; } = null!;
        private string ConnectionString => $"Server=(localdb)\\BoardTrace;Database={name};Integrated Security=true;TrustServerCertificate=true";
        private static string MasterConnection => "Server=(localdb)\\BoardTrace;Database=master;Integrated Security=true;TrustServerCertificate=true";

        public static async Task<PlcServer> CreateAsync()
        {
            var server = new PlcServer();
            await using (var master = new SqlConnection(MasterConnection))
            {
                await master.OpenAsync();
                await using var command = master.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{server.name}]";
                await command.ExecuteNonQueryAsync();
            }
            try
            {
                server.factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["ConnectionStrings:BoardTrace"] = server.ConnectionString })));
                server.StationA = server.factory.CreateClient();
                server.StationB = server.factory.CreateClient();
                server.Human = server.factory.CreateClient();
                await server.SeedAsync();
                await Login(server.StationA, "station-a", "Test!Station1");
                await Login(server.StationB, "station-b", "Test!Station2");
                await Login(server.Human, "operator", "Test!Operator1");
                return server;
            }
            catch { await server.DisposeAsync(); throw; }
        }

        private async Task SeedAsync()
        {
            using var scope = factory!.Services.CreateScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<BoardTraceUser>>();
            foreach (var role in new[] { "Operator", "Station" })
                Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
            foreach (var (id, name, role, station, password) in new[]
            {
                ("operator-plc-test", "operator", "Operator", (string?)null, "Test!Operator1"),
                ("station-a-plc-test", "station-a", "Station", "STATION-A", "Test!Station1"),
                ("station-b-plc-test", "station-b", "Station", "STATION-B", "Test!Station2")
            })
            {
                var user = new BoardTraceUser { Id = id, UserName = name, DisplayName = name, StationId = station };
                Assert.True((await users.CreateAsync(user, password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            }
        }

        private static async Task Login(HttpClient client, string userName, string password)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task<int> CountAsync()
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM dbo.Inspections";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            StationA?.Dispose(); StationB?.Dispose(); Human?.Dispose(); factory?.Dispose();
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
}
