using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Recipes;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;

namespace BoardTrace.Server.Tests;

public sealed class RecipeApiTests
{
    private static SaveRecipeDraft Draft(double minRecall = 0) => new("classical-check",
        new ClassicalRecipeDefinition(new RecipeClassicalSettings()), new RecipeTargets(0, minRecall, 10000));

    [Fact]
    public async Task EngineerStartsRealValidationAndHistoryKeepsOriginalTargetsAndFailureDenominator()
    {
        await using var server = await RecipeServer.CreateAsync(corruptFirst: true);
        using var created = await server.Engineer.PostAsJsonAsync("/api/recipes/drafts", Draft());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = (await created.Content.ReadFromJsonAsync<RecipeDraftView>())!;
        using var queued = await server.Engineer.PostAsync($"/api/recipes/drafts/{draft.Id}/validations", null);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var run = (await queued.Content.ReadFromJsonAsync<RecipeValidationView>())!;
        Assert.Equal(draft.SnapshotHash, run.Snapshot.SnapshotHash);
        using var changed = await server.Engineer.PutAsJsonAsync($"/api/recipes/drafts/{draft.Id}", Draft(0.9));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var updated = (await changed.Content.ReadFromJsonAsync<RecipeDraftView>())!;
        Assert.NotEqual(draft.SnapshotHash, updated.SnapshotHash);

        RecipeValidationView? completed = null;
        for (var i = 0; i < 120; i++)
        {
            completed = await server.Engineer.GetFromJsonAsync<RecipeValidationView>($"/api/recipes/validations/{run.Id}");
            if (completed!.Status is "Completed" or "Failed") break;
            await Task.Delay(250);
        }
        Assert.Equal("Completed", completed!.Status);
        Assert.Equal(200, completed.Processed);
        Assert.Equal(0, completed.Snapshot.Targets.MinRecall);
        Assert.Equal(0.9, updated.Targets.MinRecall);
        Assert.NotNull(completed.Report);
        Assert.Equal(200, completed.Report.Rows.Count);
        Assert.Equal(1, completed.Report.ExecutionFailures);
        Assert.Equal("Failed", completed.Report.Rows[0].Status);
        Assert.Equal(1, completed.Report.Rows[0].Fn);
        Assert.False(completed.Report.MeetsTargets);
        Assert.Equal(64, completed.Report.AlgorithmAssemblySha256.Length);
        Assert.True(completed.Report.ColdSampleMs > 0); // First successful sample is the cold run.
    }

    [Fact]
    public async Task HumanMayReadButOnlyEngineerMayEditOrStartValidation()
    {
        await using var server = await RecipeServer.CreateAsync(corruptFirst: false);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await server.Anonymous.GetAsync("/api/recipes/drafts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await server.Operator.PostAsJsonAsync("/api/recipes/drafts", Draft())).StatusCode);
        using var created = await server.Engineer.PostAsJsonAsync("/api/recipes/drafts", Draft());
        var draft = (await created.Content.ReadFromJsonAsync<RecipeDraftView>())!;
        Assert.Equal(HttpStatusCode.OK,
            (await server.Operator.GetAsync($"/api/recipes/drafts/{draft.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await server.Operator.PutAsJsonAsync($"/api/recipes/drafts/{draft.Id}", Draft())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await server.Operator.PostAsync($"/api/recipes/drafts/{draft.Id}/validations", null)).StatusCode);
        await server.ChangeManifestAsync();
        using var conflict = await server.Engineer.PostAsync($"/api/recipes/drafts/{draft.Id}/validations", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("固定验证清单已改变", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task RestartMarksInterruptedRunFailedAndCompletesPreviouslyQueuedRun()
    {
        await using var server = await RecipeServer.CreateAsync(corruptFirst: false);
        using var created = await server.Engineer.PostAsJsonAsync("/api/recipes/drafts", Draft());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = (await created.Content.ReadFromJsonAsync<RecipeDraftView>())!;
        var interruptedId = Guid.NewGuid();
        var queuedId = Guid.NewGuid();
        await server.StopAndSeedRecoveryRunsAsync(draft, interruptedId, queuedId);
        await server.RestartAsync();

        var interrupted = await server.Engineer.GetFromJsonAsync<RecipeValidationView>(
            $"/api/recipes/validations/{interruptedId}");
        Assert.Equal("Failed", interrupted!.Status);
        Assert.Contains("中断", interrupted.Error);
        Assert.Null(interrupted.Report);
        Assert.NotNull(interrupted.CompletedAt);

        RecipeValidationView? queued = null;
        for (var i = 0; i < 120; i++)
        {
            queued = await server.Engineer.GetFromJsonAsync<RecipeValidationView>(
                $"/api/recipes/validations/{queuedId}");
            if (queued!.Status is "Completed" or "Failed") break;
            await Task.Delay(250);
        }
        Assert.Equal("Completed", queued!.Status);
        Assert.Equal(200, queued.Processed);
        Assert.Equal(200, queued.Report!.Rows.Count);
        Assert.Equal(0, queued.Report.ExecutionFailures);
        Assert.All(queued.Report.Rows, row => Assert.Equal("Completed", row.Status));
    }

    private sealed class RecipeServer : IAsyncDisposable
    {
        private readonly string name = "BoardTrace_Integration_" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "BoardTrace_Recipe_" + Guid.NewGuid().ToString("N"));
        private WebApplicationFactory<Program>? factory;
        public HttpClient Engineer { get; private set; } = null!;
        public HttpClient Operator { get; private set; } = null!;
        public HttpClient Anonymous { get; private set; } = null!;
        private string ConnectionString => $"Server=(localdb)\\BoardTrace;Database={name};Integrated Security=true;TrustServerCertificate=true";
        private const string Master = "Server=(localdb)\\BoardTrace;Database=master;Integrated Security=true;TrustServerCertificate=true";

        private WebApplicationFactory<Program> NewFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:BoardTrace"] = ConnectionString,
                    ["RecipeValidation:DataRoot"] = Path.Combine(directory, "data"),
                    ["RecipeValidation:ManifestRoot"] = Path.Combine(directory, "manifests") })));

        public static async Task<RecipeServer> CreateAsync(bool corruptFirst)
        {
            var server = new RecipeServer();
            Directory.CreateDirectory(Path.Combine(server.directory, "manifests", "inputs"));
            Directory.CreateDirectory(Path.Combine(server.directory, "manifests", "truth"));
            Directory.CreateDirectory(Path.Combine(server.directory, "data"));
            using var picture = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
            Cv2.Rectangle(picture, new Rect(100, 100, 300, 20), Scalar.Black, -1);
            var good = picture.ImEncode(".png");
            var bad = new byte[] { 1, 2, 3 };
            var inputLines = new List<string>();
            var truthLines = new List<string>();
            for (var i = 0; i < 200; i++)
            {
                var tested = i == 0 && corruptFirst ? bad : good;
                var file = $"{i}.png";
                await File.WriteAllBytesAsync(Path.Combine(server.directory, "data", file), tested);
                inputLines.Add(JsonSerializer.Serialize(new { sampleId = i.ToString("D3"), image = file,
                    imageSha256 = Convert.ToHexStringLower(SHA256.HashData(tested)), reference = "reference.png",
                    referenceSha256 = Convert.ToHexStringLower(SHA256.HashData(good)) }));
                truthLines.Add(JsonSerializer.Serialize(new { sampleId = i.ToString("D3"),
                    defects = i == 0 ? new[] { new { box = new[] { 10, 10, 30, 30 }, classId = 1 } } : [] }));
            }
            await File.WriteAllBytesAsync(Path.Combine(server.directory, "data", "reference.png"), good);
            await File.WriteAllLinesAsync(Path.Combine(server.directory, "manifests", "inputs", "validation.jsonl"), inputLines);
            await File.WriteAllLinesAsync(Path.Combine(server.directory, "manifests", "truth", "validation.jsonl"), truthLines);
            await using (var master = new SqlConnection(Master))
            {
                await master.OpenAsync();
                await using var create = master.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{server.name}]";
                await create.ExecuteNonQueryAsync();
            }
            try
            {
                server.factory = server.NewFactory();
                server.Anonymous = server.factory.CreateClient();
                using var scope = server.factory.Services.CreateScope();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<BoardTraceUser>>();
                foreach (var role in new[] { "Operator", "ProcessEngineer" })
                    Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
                foreach (var (name, role) in new[] { ("engineer", "ProcessEngineer"), ("operator", "Operator") })
                {
                    var user = new BoardTraceUser { UserName = name, DisplayName = name };
                    Assert.True((await users.CreateAsync(user, "Test!Recipe1")).Succeeded);
                    Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                }
                server.Engineer = server.factory.CreateClient();
                server.Operator = server.factory.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await server.Engineer.PostAsJsonAsync("/api/auth/login",
                    new LoginRequest("engineer", "Test!Recipe1"))).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await server.Operator.PostAsJsonAsync("/api/auth/login",
                    new LoginRequest("operator", "Test!Recipe1"))).StatusCode);
                return server;
            }
            catch { await server.DisposeAsync(); throw; }
        }

        public async Task StopAndSeedRecoveryRunsAsync(RecipeDraftView draft, Guid interruptedId, Guid queuedId)
        {
            Engineer.Dispose(); Operator.Dispose(); Anonymous.Dispose(); factory!.Dispose();
            factory = null;
            var options = new DbContextOptionsBuilder<BoardTraceDbContext>().UseSqlServer(ConnectionString).Options;
            await using var db = new BoardTraceDbContext(options);
            var truthPath = Path.Combine(directory, "manifests", "truth", "validation.jsonl");
            var truthHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(truthPath)));
            foreach (var (id, status) in new[] { (interruptedId, "Running"), (queuedId, "Queued") })
                db.ValidationRuns.Add(new ValidationRun { Id = id, DraftId = draft.Id, Status = status,
                    Name = draft.Name, DefinitionJson = JsonSerializer.Serialize<RecipeDefinition>(draft.Definition),
                    TargetsJson = JsonSerializer.Serialize(draft.Targets), SnapshotHash = draft.SnapshotHash,
                    ManifestHash = draft.DataManifestSha256, TruthHash = truthHash, Total = 200,
                    CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        public async Task ChangeManifestAsync()
        {
            var path = Path.Combine(directory, "manifests", "inputs", "validation.jsonl");
            var lines = await File.ReadAllLinesAsync(path);
            lines[0] = lines[0].Replace("\"sampleId\":\"000\"", "\"sampleId\":\"changed\"", StringComparison.Ordinal);
            await File.WriteAllLinesAsync(path, lines);
        }

        public async Task RestartAsync()
        {
            factory = NewFactory();
            Anonymous = factory.CreateClient();
            Engineer = factory.CreateClient();
            Operator = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await Engineer.PostAsJsonAsync("/api/auth/login",
                new LoginRequest("engineer", "Test!Recipe1"))).StatusCode);
        }

        public async ValueTask DisposeAsync()
        {
            Engineer?.Dispose(); Operator?.Dispose(); Anonymous?.Dispose(); factory?.Dispose();
            if (!name.StartsWith("BoardTrace_Integration_", StringComparison.Ordinal) ||
                name.Length != "BoardTrace_Integration_".Length + 32 ||
                !name.AsSpan("BoardTrace_Integration_".Length).ToString().All(Uri.IsHexDigit))
                throw new InvalidOperationException("Refusing unexpected test database cleanup.");
            await using var master = new SqlConnection(Master);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
