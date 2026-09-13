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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;

namespace BoardTrace.Server.Tests;

public sealed class PublicationTests
{
    private static SaveRecipeDraft Draft(int padding = 0) => new("generated-image-publication-test",
        new RecipeClassicalSettings(BoxPadding: padding), new RecipeTargets(0.99, 0.99, 500));

    [Fact]
    public async Task LowDraftTargetsCannotBypassMissingUnfrozenOrHigherIndependentTargets()
    {
        await using var server = await PublicationServer.CreateAsync();
        var draft = await server.CreateDraftAsync(Draft() with
        {
            Settings = new RecipeClassicalSettings(MinimumArea: 10000, BoxPadding: 0),
            Targets = new RecipeTargets(0, 0, 500)
        });
        var run = await server.ValidateAsync(draft.Id);
        Assert.True(run.Report!.MeetsTargets);
        Assert.Equal(200, run.Report.Fn);
        Assert.Equal(0, run.Report.Recall);

        server.ConfigurePolicyPath(null);
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "未配置");
        server.ConfigurePolicyPath(server.ReleasePolicyPath);
        File.Delete(server.ReleasePolicyPath);
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "文件");
        await server.WritePolicyAsync(false, new RecipeTargets(1, 1, 500));
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "尚未冻结");
        await server.WritePolicyAsync(true, new RecipeTargets(1, 1, 500));
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "独立冻结目标");
        Assert.Equal(0, await server.CountVersionsAsync());
    }

    [Fact]
    public async Task ReleasePolicyIsReadFreshForHumansAndInvalidTargetsDoNotBecomeFrozen()
    {
        await using var server = await PublicationServer.CreateAsync();
        foreach (var client in new[] { server.Engineer, server.Operator, server.Quality })
        {
            using var response = await client.GetAsync("/api/recipes/release-policy");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var policy = (await response.Content.ReadFromJsonAsync<RecipeReleasePolicy>())!;
            Assert.True(policy.IsFrozen);
            Assert.Equal(new RecipeTargets(1, 1, 500), policy.Targets);
            Assert.Null(policy.Reason);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Station.GetAsync("/api/recipes/release-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Anonymous.GetAsync("/api/recipes/release-policy")).StatusCode);
        server.ConfigurePolicyPath(null);
        await AssertUnavailable("未配置");
        server.ConfigurePolicyPath(server.ReleasePolicyPath);
        File.Delete(server.ReleasePolicyPath);
        await AssertUnavailable("文件");
        await server.WritePolicyAsync(false, new RecipeTargets(1, 1, 500));
        await AssertUnavailable("尚未冻结");
        // Missing fields must not deserialize as zero and silently weaken the release gate.
        await File.WriteAllTextAsync(server.ReleasePolicyPath, """{"qualityTargetsFrozen":true,"qualityTargets":{"maxP95Ms":500}}""");
        await AssertUnavailable("格式");
        await server.WritePolicyAsync(true, new RecipeTargets(-1, 1, 500));
        await AssertUnavailable("格式");

        async Task AssertUnavailable(string message)
        {
            var policy = (await server.Engineer.GetFromJsonAsync<RecipeReleasePolicy>("/api/recipes/release-policy"))!;
            Assert.False(policy.IsFrozen);
            Assert.Null(policy.Targets);
            Assert.Contains(message, policy.Reason);
        }
    }

    [Fact]
    public async Task ConcurrentPublicationFreezesRealPassedValidationAndSurvivesOriginalAssetDeletion()
    {
        await using var server = await PublicationServer.CreateAsync();
        var draft = await server.CreateDraftAsync(Draft());
        var run = await server.ValidateAsync(draft.Id);
        Assert.True(run.Report!.MeetsTargets);
        Assert.Equal(200, run.Report.Tp);
        Assert.Equal(1, run.Report.Precision);
        Assert.Equal(1, run.Report.Recall);

        await server.WritePolicyAsync(true, new RecipeTargets(1, 1, run.Report.P95Ms / 2));
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "独立冻结目标");
        await server.WritePolicyAsync(true, new RecipeTargets(1, 1, 500));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            server.Engineer.PostAsJsonAsync($"/api/recipes/drafts/{draft.Id}/publish", new PublishRecipeRequest(run.Id))));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Created }));
        var versions = await Task.WhenAll(responses.Select(async response =>
        {
            using (response) return (await response.Content.ReadFromJsonAsync<PublishedRecipeVersion>())!;
        }));
        var version = versions[0];
        await server.AssignAsync(version.Bundle.VersionId);
        Assert.All(versions, other => Assert.Equal(version.BundleHash, other.BundleHash));
        Assert.Equal(version.BundleHash, PublishedRecipeTransfer.Hash(version.Bundle));
        Assert.Equal("publication-engineer", version.Bundle.PublishedByName);
        Assert.Equal(200, version.Bundle.References.Count);
        Assert.Equal(2, version.Bundle.References.Select(reference => reference.AssetId).Distinct().Count());
        Assert.Equal(new PublishedRecipeInput(640, 640, true), version.Bundle.Input);
        Assert.Equal(new RecipeTargets(1, 1, 500), version.Bundle.ReleaseTargets);
        var originalBundle = await server.Station.GetByteArrayAsync($"/api/recipes/versions/{version.Bundle.VersionId}/bundle");
        foreach (var reference in version.Bundle.References.DistinctBy(reference => reference.AssetId))
        {
            var bytes = await server.Station.GetByteArrayAsync($"/api/recipe-assets/{reference.AssetId}");
            Assert.Equal(reference.ByteLength, bytes.Length);
            Assert.Equal(reference.Sha256, PublicationServer.Hash(bytes));
        }

        using var changed = await server.Engineer.PutAsJsonAsync($"/api/recipes/drafts/{draft.Id}", Draft(1));
        changed.EnsureSuccessStatusCode();
        var nextRun = await server.ValidateAsync(draft.Id);
        await server.WritePolicyAsync(true, new RecipeTargets(0.999, 0.999, 400));
        using var nextPublication = await server.Engineer.PostAsJsonAsync($"/api/recipes/drafts/{draft.Id}/publish", new PublishRecipeRequest(nextRun.Id));
        Assert.Equal(HttpStatusCode.Created, nextPublication.StatusCode);
        var nextVersion = (await nextPublication.Content.ReadFromJsonAsync<PublishedRecipeVersion>())!;
        Assert.NotEqual(version.Bundle.VersionId, nextVersion.Bundle.VersionId);
        Assert.Equal(1, nextVersion.Bundle.Settings.BoxPadding);
        Assert.Equal(0, version.Bundle.Settings.BoxPadding);
        Assert.Equal(new RecipeTargets(0.999, 0.999, 400), nextVersion.Bundle.ReleaseTargets);

        File.Delete(server.ReleasePolicyPath);
        File.Delete(server.ReferencePath(0));
        await File.WriteAllBytesAsync(server.ReferencePath(1), [1, 2, 3]);
        Assert.Equal(originalBundle, await server.Station.GetByteArrayAsync($"/api/recipes/versions/{version.Bundle.VersionId}/bundle"));
        foreach (var reference in version.Bundle.References.DistinctBy(reference => reference.AssetId))
            Assert.Equal(reference.Sha256, PublicationServer.Hash(await server.Station.GetByteArrayAsync($"/api/recipe-assets/{reference.AssetId}")));
        using var retried = await server.Engineer.PostAsJsonAsync($"/api/recipes/drafts/{draft.Id}/publish", new PublishRecipeRequest(run.Id));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(version.BundleHash, (await retried.Content.ReadFromJsonAsync<PublishedRecipeVersion>())!.BundleHash);
        var listed = await server.Engineer.GetFromJsonAsync<PublishedRecipeSummary[]>("/api/recipes/versions");
        Assert.Equal(2, listed!.Length);
        Assert.Equal(2, await server.CountVersionsAsync());
        var body = System.Text.Encoding.UTF8.GetString(originalBundle);
        Assert.DoesNotContain("truth", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tested", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicationRejectsUnmetTargetsStaleSnapshotsAndMissingAssets()
    {
        await using var server = await PublicationServer.CreateAsync();
        var missedDraft = await server.CreateDraftAsync(Draft() with { Settings = new RecipeClassicalSettings(MinimumArea: 10000, BoxPadding: 0) });
        var missed = await server.ValidateAsync(missedDraft.Id);
        Assert.False(missed.Report!.MeetsTargets);
        Assert.Equal(200, missed.Report.Fn);
        await server.AssertRejectedAsync(missedDraft.Id, missed.Id, HttpStatusCode.Conflict, "目标");

        var draft = await server.CreateDraftAsync(Draft());
        var passed = await server.ValidateAsync(draft.Id);
        using var changed = await server.Engineer.PutAsJsonAsync($"/api/recipes/drafts/{draft.Id}", Draft(1));
        changed.EnsureSuccessStatusCode();
        await server.AssertRejectedAsync(draft.Id, passed.Id, HttpStatusCode.Conflict, "草稿");
        using var restored = await server.Engineer.PutAsJsonAsync($"/api/recipes/drafts/{draft.Id}", Draft());
        restored.EnsureSuccessStatusCode();
        File.Delete(server.ReferencePath(0));
        await server.AssertRejectedAsync(draft.Id, passed.Id, HttpStatusCode.ServiceUnavailable, "参考");
        Assert.Equal(0, await server.CountVersionsAsync());
    }

    [Fact]
    public async Task PublicationRechecksActualMetricsPopulationAlgorithmAndAssetIdentity()
    {
        await using var server = await PublicationServer.CreateAsync();
        var draft = await server.CreateDraftAsync(Draft());
        var run = await server.ValidateAsync(draft.Id);
        var report = run.Report!;
        // Perturb a real report in this isolated database to prove the gate uses business facts,
        // not just the previously computed MeetsTargets flag.
        await server.ReplaceReportAsync(run.Id, report with { Precision = 0.1, MeetsTargets = true });
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "目标");
        await server.ReplaceReportAsync(run.Id, report with { P95Ms = 1000, MeetsTargets = true });
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "目标");
        await server.ReplaceReportAsync(run.Id, report with
        {
            Rows = report.Rows.Skip(1).Prepend(report.Rows[0] with { Status = "Failed", Error = "isolated report corruption" }).ToArray()
        });
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "执行失败");
        await server.ReplaceReportAsync(run.Id, report with { Rows = report.Rows.Take(199).ToArray() });
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "200");
        await server.ReplaceReportAsync(run.Id, report with { Rows = report.Rows.Take(199).Append(report.Rows[0]).ToArray() });
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "样本");
        await server.ReplaceReportAsync(run.Id, report with { AlgorithmAssemblySha256 = new string('0', 64) });
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "算法");
        await server.ReplaceReportAsync(run.Id, report);
        var referenceBytes = await File.ReadAllBytesAsync(server.ReferencePath(0));
        await File.WriteAllBytesAsync(server.ReferencePath(0), [1, 2, 3]);
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "参考");
        await File.WriteAllBytesAsync(server.ReferencePath(0), referenceBytes);
        await File.AppendAllTextAsync(server.InputManifestPath, "\n");
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "清单");
        Assert.Equal(0, await server.CountVersionsAsync());
    }

    [Fact]
    public async Task OnlyEngineerPublishesAndStationReadsOnlyPublishedBundleAndReferences()
    {
        await using var server = await PublicationServer.CreateAsync();
        var draft = await server.CreateDraftAsync(Draft());
        var run = await server.ValidateAsync(draft.Id);
        foreach (var client in new[] { server.Operator, server.Quality, server.Station })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/recipes/drafts/{draft.Id}/publish", new PublishRecipeRequest(run.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Anonymous.GetAsync("/api/recipes/versions")).StatusCode);
        using var published = await server.Engineer.PostAsJsonAsync($"/api/recipes/drafts/{draft.Id}/publish", new PublishRecipeRequest(run.Id));
        published.EnsureSuccessStatusCode();
        var version = (await published.Content.ReadFromJsonAsync<PublishedRecipeVersion>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Station.GetAsync($"/api/recipes/versions/{version.Bundle.VersionId}/bundle")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Station.GetAsync($"/api/recipe-assets/{version.Bundle.References[0].AssetId}")).StatusCode);
        await server.AssignAsync(version.Bundle.VersionId);
        foreach (var client in new[] { server.Engineer, server.Operator, server.Quality })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/recipes/versions")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/recipes/versions/{version.Bundle.VersionId}")).StatusCode);
        }
        foreach (var path in new[] { "/api/recipes/versions", $"/api/recipes/versions/{version.Bundle.VersionId}", "/api/recipes/drafts", $"/api/recipes/validations/{run.Id}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await server.Station.GetAsync(path)).StatusCode);
        foreach (var client in new[] { server.Engineer, server.Operator, server.Quality, server.Station })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/recipes/versions/{version.Bundle.VersionId}/bundle")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/recipe-assets/{version.Bundle.References[0].AssetId}")).StatusCode);
        }
    }

    private sealed class PublicationServer : IAsyncDisposable
    {
        private readonly string name = "BoardTrace_Publication_" + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "BoardTrace_Publication_" + Guid.NewGuid().ToString("N"));
        private WebApplicationFactory<Program>? factory;
        public HttpClient Engineer { get; private set; } = null!;
        public HttpClient Operator { get; private set; } = null!;
        public HttpClient Quality { get; private set; } = null!;
        public HttpClient Station { get; private set; } = null!;
        public HttpClient Anonymous { get; private set; } = null!;
        private string ConnectionString => $"Server=(localdb)\\BoardTrace;Database={name};Integrated Security=true;TrustServerCertificate=true";
        private const string Master = "Server=(localdb)\\BoardTrace;Database=master;Integrated Security=true;TrustServerCertificate=true";
        public string InputManifestPath => Path.Combine(directory, "manifests", "inputs", "validation.jsonl");
        public string ReleasePolicyPath => Path.Combine(directory, "frozen-release.json");
        public string ReferencePath(int number) => Path.Combine(directory, "data", $"reference-{number}.png");
        public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        public Task WritePolicyAsync(bool frozen, RecipeTargets targets) => File.WriteAllTextAsync(ReleasePolicyPath,
            JsonSerializer.Serialize(new { qualityTargetsFrozen = frozen, qualityTargets = targets }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        public void ConfigurePolicyPath(string? path) => factory!.Services.GetRequiredService<IConfiguration>()["RecipePublication:ReleasePolicyPath"] = path;

        public static async Task<PublicationServer> CreateAsync()
        {
            var server = new PublicationServer();
            Directory.CreateDirectory(Path.Combine(server.directory, "data"));
            Directory.CreateDirectory(Path.Combine(server.directory, "manifests", "inputs"));
            Directory.CreateDirectory(Path.Combine(server.directory, "manifests", "truth"));
            var inputs = new List<string>();
            var truths = new List<string>();
            for (var group = 0; group < 2; group++)
            {
                using var reference = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
                Cv2.Rectangle(reference, new Rect(83, 65, 300, 35), Scalar.Black, -1);
                Cv2.Rectangle(reference, new Rect(348, 65, 35, 440), Scalar.Black, -1);
                Cv2.Circle(reference, new Point(200, 450), 60 + group * 5, Scalar.Black, -1);
                var referenceBytes = reference.ImEncode(".png");
                await File.WriteAllBytesAsync(server.ReferencePath(group), referenceBytes);
                for (var index = 0; index < 100; index++)
                {
                    var sampleId = $"generated-{group}-{index:D3}";
                    var x = 470 + index % 8 * 3;
                    var y = 280 + index % 11 * 3;
                    using var tested = reference.Clone();
                    Cv2.Rectangle(tested, new Rect(x, y, 15, 18), Scalar.Black, -1);
                    var bytes = tested.ImEncode(".png");
                    await File.WriteAllBytesAsync(Path.Combine(server.directory, "data", sampleId + ".png"), bytes);
                    inputs.Add(JsonSerializer.Serialize(new { sampleId, image = sampleId + ".png", imageSha256 = Hash(bytes),
                        reference = $"reference-{group}.png", referenceSha256 = Hash(referenceBytes) }));
                    truths.Add(JsonSerializer.Serialize(new { sampleId, defects = new[] { new { box = new[] { x, y, x + 15, y + 18 } } } }));
                }
            }
            await File.WriteAllLinesAsync(server.InputManifestPath, inputs);
            await File.WriteAllLinesAsync(Path.Combine(server.directory, "manifests", "truth", "validation.jsonl"), truths);
            await server.WritePolicyAsync(true, new RecipeTargets(1, 1, 500));
            await using (var master = new SqlConnection(Master))
            {
                await master.OpenAsync();
                await using var create = master.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{server.name}]";
                await create.ExecuteNonQueryAsync();
            }
            try
            {
                server.factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:BoardTrace"] = server.ConnectionString,
                        ["RecipeValidation:DataRoot"] = Path.Combine(server.directory, "data"),
                        ["RecipePublication:ReleasePolicyPath"] = server.ReleasePolicyPath,
                        ["RecipeValidation:ManifestRoot"] = Path.Combine(server.directory, "manifests") })));
                server.Anonymous = server.factory.CreateClient();
                using var scope = server.factory.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<BoardTraceUser>>();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                foreach (var role in new[] { "ProcessEngineer", "Operator", "QualityEngineer", "Station" })
                {
                    Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
                    var user = new BoardTraceUser { UserName = role, DisplayName = role == "ProcessEngineer" ? "publication-engineer" : role,
                        StationId = role == "Station" ? "PUBLICATION-STATION" : null };
                    Assert.True((await users.CreateAsync(user, "Test!Publication1")).Succeeded);
                    Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                }
                async Task<HttpClient> Login(string role)
                {
                    var client = server.factory.CreateClient();
                    using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(role, "Test!Publication1"));
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    return client;
                }
                server.Engineer = await Login("ProcessEngineer");
                server.Operator = await Login("Operator");
                server.Quality = await Login("QualityEngineer");
                server.Station = await Login("Station");
                return server;
            }
            catch { await server.DisposeAsync(); throw; }
        }

        public async Task<RecipeDraftView> CreateDraftAsync(SaveRecipeDraft input)
        {
            using var response = await Engineer.PostAsJsonAsync("/api/recipes/drafts", input);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<RecipeDraftView>())!;
        }

        public async Task AssignAsync(Guid versionId)
        {
            using var response = await Engineer.PostAsJsonAsync("/api/batches", new CreateBatchRequest("ISOLATED-PUBLICATION-BATCH", "PCB", "TOP", 2, "PUBLICATION-STATION", versionId));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        public async Task<RecipeValidationView> ValidateAsync(Guid draftId)
        {
            using var response = await Engineer.PostAsync($"/api/recipes/drafts/{draftId}/validations", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var run = (await response.Content.ReadFromJsonAsync<RecipeValidationView>())!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (run.Status is "Queued" or "Running")
            {
                await Task.Delay(100, timeout.Token);
                run = (await Engineer.GetFromJsonAsync<RecipeValidationView>($"/api/recipes/validations/{run.Id}", timeout.Token))!;
            }
            Assert.Equal("Completed", run.Status);
            Assert.Equal(200, run.Processed);
            Assert.Equal(0, run.Report!.ExecutionFailures);
            return run;
        }

        public async Task AssertRejectedAsync(Guid draftId, Guid runId, HttpStatusCode status, string message)
        {
            using var response = await Engineer.PostAsJsonAsync($"/api/recipes/drafts/{draftId}/publish", new PublishRecipeRequest(runId));
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains(message, await response.Content.ReadAsStringAsync());
        }

        public async Task ReplaceReportAsync(Guid runId, RecipeValidationReport report)
        {
            using var scope = factory!.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>();
            var run = await db.ValidationRuns.FindAsync(runId);
            run!.ReportJson = JsonSerializer.Serialize(report);
            await db.SaveChangesAsync();
        }

        public async Task<int> CountVersionsAsync()
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM RecipeVersions";
            return Convert.ToInt32(await query.ExecuteScalarAsync());
        }

        public async ValueTask DisposeAsync()
        {
            Engineer?.Dispose(); Operator?.Dispose(); Quality?.Dispose(); Station?.Dispose(); Anonymous?.Dispose(); factory?.Dispose();
            if (!name.StartsWith("BoardTrace_Publication_", StringComparison.Ordinal) ||
                name.Length != "BoardTrace_Publication_".Length + 32 || !name.AsSpan("BoardTrace_Publication_".Length).ToString().All(Uri.IsHexDigit))
                throw new InvalidOperationException("Refusing unexpected publication test database cleanup.");
            await using var master = new SqlConnection(Master);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
