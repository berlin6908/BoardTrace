using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Recipes;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BoardTrace.Server.Tests;

public sealed class BatchApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedBatchRejectsAnotherArchiveEvenBeforeAnyProductionWasUploaded(bool uploadProduction)
    {
        await using var server = await BatchServer.CreateAsync();
        var batch = await server.CreateBatch();
        var first = server.Record(batch, InspectionPurpose.FirstArticle);
        await server.Upload(first);
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval",
            new ApproveFirstArticleRequest(first.Id)), HttpStatusCode.Created);
        var archiveId = Guid.NewGuid();
        var request = new { StationId = "STATION-A", RecipeBundleHash = server.Version.BundleHash, ArchiveId = archiveId };
        using var start = await server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", request);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var originalSession = (await start.Content.ReadFromJsonAsync<BatchExecutionSession>())!;
        if (uploadProduction)
            await server.Upload(server.Record(batch, InspectionPurpose.Production) with
            {
                ExecutionSessionId = originalSession.Id, ProductionSequence = 1,
                StartedAt = originalSession.IssuedAt, CompletedAt = originalSession.IssuedAt.AddMilliseconds(1)
            });
        using var emptyArchive = await server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions",
            request with { ArchiveId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, emptyArchive.StatusCode);
        Assert.Contains("SQLite", await emptyArchive.Content.ReadAsStringAsync());
        await server.Login(server.Operator, "operator-2");
        using var nextShift = await server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", request);
        Assert.Equal(HttpStatusCode.Created, nextShift.StatusCode);
        var nextSession = (await nextShift.Content.ReadFromJsonAsync<BatchExecutionSession>())!;
        Assert.Equal("operator-2", nextSession.OperatorId);
        Assert.NotEqual(originalSession.Id, nextSession.Id);
    }

    [Fact]
    public async Task ConcurrentFirstStartsBindOnlyOneArchiveAndFailureRollsBackBinding()
    {
        await using var server = await BatchServer.CreateAsync();
        var batch = await server.CreateBatch();
        var first = server.Record(batch, InspectionPurpose.FirstArticle);
        await server.Upload(first);
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval",
            new ApproveFirstArticleRequest(first.Id)), HttpStatusCode.Created);
        var requests = new[] { Guid.NewGuid(), Guid.NewGuid() }.Select(archiveId =>
            new { StationId = "STATION-A", RecipeBundleHash = server.Version.BundleHash, ArchiveId = archiveId }).ToArray();
        await server.Execute("CREATE TRIGGER dbo.RejectSession ON dbo.BatchExecutionSessions AFTER INSERT AS BEGIN THROW 51000, 'fixture session save failure', 1; END");
        await server.Status(server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", requests[0] with { ArchiveId = Guid.NewGuid() }), HttpStatusCode.InternalServerError);
        await server.Execute("DROP TRIGGER dbo.RejectSession");
        var replies = await Task.WhenAll(requests.Select(request => server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", request)));
        Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Created);
        Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Conflict);
        for (var index = 0; index < replies.Length; index++)
        {
            var expected = replies[index].IsSuccessStatusCode ? HttpStatusCode.OK : HttpStatusCode.Conflict;
            await server.Status(server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", requests[index]), expected);
            replies[index].Dispose();
        }
    }

    [Fact]
    public async Task CreationIsIdempotentAndStationAssignmentsAreExclusiveAndProtected()
    {
        await using var server = await BatchServer.CreateAsync();
        var request = server.Request();
        var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => server.Engineer.PostAsJsonAsync("/api/batches", request)));
        Assert.Single(replies, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(replies, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        var batch = (await replies[0].Content.ReadFromJsonAsync<BatchDetails>())!;
        foreach (var reply in replies) reply.Dispose();
        Assert.Equal(BatchStatus.AwaitingFirstArticle, batch.Status);
        await server.Status(server.Engineer.PostAsJsonAsync("/api/batches", request with { PlannedQuantity = 5 }), HttpStatusCode.Conflict);
        await server.Status(server.Engineer.PostAsJsonAsync("/api/batches", request with { BatchNumber = "OTHER" }), HttpStatusCode.Conflict);
        await server.Status(server.Engineer.PostAsJsonAsync("/api/batches", request with { BatchNumber = "UNPUBLISHED", StationId = "STATION-B", RecipeVersionId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        await server.Status(server.Operator.PostAsJsonAsync("/api/batches", request), HttpStatusCode.Forbidden);
        var stations = (await server.Engineer.GetFromJsonAsync<StationSummary[]>("/api/stations"))!;
        Assert.Equal(new[] { "STATION-A", "STATION-B" }, stations.Select(station => station.StationId));
        Assert.Single((await server.Device.GetFromJsonAsync<BatchSummary[]>("/api/station/batches"))!);
        Assert.Empty((await server.OtherDevice.GetFromJsonAsync<BatchSummary[]>("/api/station/batches"))!);
        await server.Status(server.OtherDevice.GetAsync($"/api/station/batches/{batch.Batch.Id}/package"), HttpStatusCode.Forbidden);
        await server.Status(server.Device.GetAsync("/api/batches"), HttpStatusCode.Forbidden);
        await server.Status(server.Device.GetAsync("/api/stations"), HttpStatusCode.Forbidden);
        var package = (await server.Device.GetFromJsonAsync<BatchPackage>($"/api/station/batches/{batch.Batch.Id}/package"))!;
        Assert.Equal(server.Version.BundleHash, package.Recipe.BundleHash);
        foreach (var path in new[] { $"/api/recipes/versions/{server.Version.Bundle.VersionId}/bundle", $"/api/recipe-assets/{server.Version.Bundle.References[0].AssetId}" })
        {
            await server.Status(server.Device.GetAsync(path), HttpStatusCode.OK);
            await server.Status(server.OtherDevice.GetAsync(path), HttpStatusCode.Forbidden);
            await server.Status(server.Quality.GetAsync(path), HttpStatusCode.OK);
        }
        Assert.Single((await server.Quality.GetFromJsonAsync<BatchSummary[]>("/api/batches?stationId=STATION-A&status=AwaitingFirstArticle"))!);
    }

    [Fact]
    public async Task ApprovalRequiresAnUploadedPassingFirstArticleAndConcurrentApprovalCannotOverwrite()
    {
        await using var server = await BatchServer.CreateAsync();
        var batch = await server.CreateBatch();
        await server.Status(server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", new StartBatchRequest("STATION-A", server.Version.BundleHash, server.ArchiveId)), HttpStatusCode.Conflict);
        var failed = server.Record(batch, InspectionPurpose.FirstArticle) with
        {
            ExecutionStatus = InspectionExecution.Failed, Decision = QualityDecision.NotEvaluated,
            TestedImage = null, ReferenceImage = null, Error = "isolated acquisition failure"
        };
        await server.Upload(failed);
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(failed.Id)), HttpStatusCode.Conflict);
        var qualityFail = server.Record(batch, InspectionPurpose.FirstArticle) with { Decision = QualityDecision.Fail, Defects = [new DefectBox([1, 1, 4, 4], null, 1, 9)] };
        await server.Upload(qualityFail);
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(qualityFail.Id)), HttpStatusCode.Conflict);
        var engineering = server.Record(batch, InspectionPurpose.EngineeringReplay) with { BatchId = null };
        await server.Upload(engineering);
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(engineering.Id)), HttpStatusCode.Conflict);
        var missingImage = server.Record(batch, InspectionPurpose.FirstArticle);
        await server.Upload(missingImage);
        await server.Execute($"DELETE FROM InspectionImages WHERE InspectionId='{missingImage.Id}' AND Kind='reference'");
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(missingImage.Id)), HttpStatusCode.Conflict);
        var pass = server.Record(batch, InspectionPurpose.FirstArticle);
        var second = server.Record(batch, InspectionPurpose.FirstArticle);
        await server.Upload(pass); await server.Upload(second);
        await server.Status(server.Operator.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(pass.Id)), HttpStatusCode.Forbidden);
        var approvals = await Task.WhenAll(new[] { pass.Id, second.Id }.Select(id => server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(id))));
        Assert.Single(approvals, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(approvals, response => response.StatusCode == HttpStatusCode.Conflict);
        var approved = (await approvals.Single(response => response.IsSuccessStatusCode).Content.ReadFromJsonAsync<FirstArticleApproval>())!;
        foreach (var response in approvals) response.Dispose();
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(approved.InspectionId)), HttpStatusCode.OK);
        var details = (await server.Quality.GetFromJsonAsync<BatchDetails>($"/api/batches/{batch.Batch.Id}"))!;
        Assert.Equal(BatchStatus.Approved, details.Status);
        Assert.Equal(approved, details.Approval);
        Assert.Equal(5, details.FirstArticles.Count);
        Assert.Contains(details.FirstArticles, row => row.Id == pass.Id && row.SourceKind == "ConstructedNormal");
        Assert.Equal(0, details.ReceivedProductionCount);
    }

    [Fact]
    public async Task ApprovalInsertFailureRollsBackItsBatchTransition()
    {
        await using var server = await BatchServer.CreateAsync();
        var batch = await server.CreateBatch();
        var pass = server.Record(batch, InspectionPurpose.FirstArticle);
        await server.Upload(pass);
        await server.Execute("CREATE TRIGGER dbo.RejectApproval ON dbo.FirstArticleApprovals AFTER INSERT AS BEGIN THROW 51000, 'isolated approval failure', 1; END");
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(pass.Id)), HttpStatusCode.InternalServerError);
        var details = (await server.Quality.GetFromJsonAsync<BatchDetails>($"/api/batches/{batch.Batch.Id}"))!;
        Assert.Equal(BatchStatus.AwaitingFirstArticle, details.Status);
        Assert.Null(details.Approval);
        await server.Execute("DROP TRIGGER dbo.RejectApproval");
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(pass.Id)), HttpStatusCode.Created);
    }

    [Fact]
    public async Task ProductionChecksVersionSourceSessionSequenceAndPreservesDelayedRetransmission()
    {
        await using var server = await BatchServer.CreateAsync(TimeSpan.FromSeconds(12));
        var batch = await server.CreateBatch();
        var first = server.Record(batch, InspectionPurpose.FirstArticle);
        await server.Upload(first);
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(first.Id)), HttpStatusCode.Created);
        await server.Status(server.Quality.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", new StartBatchRequest("STATION-A", server.Version.BundleHash, server.ArchiveId)), HttpStatusCode.Forbidden);
        await server.Status(server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", new StartBatchRequest("STATION-B", server.Version.BundleHash, server.ArchiveId)), HttpStatusCode.Conflict);
        var starts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", new StartBatchRequest("STATION-A", server.Version.BundleHash, server.ArchiveId))));
        Assert.Single(starts, response => response.StatusCode == HttpStatusCode.Created);
        var sessions = await Task.WhenAll(starts.Select(response => response.Content.ReadFromJsonAsync<BatchExecutionSession>()));
        foreach (var response in starts) response.Dispose();
        var session = sessions[0]!;
        Assert.All(sessions, value => Assert.Equal(session, value));
        Assert.Equal(server.OperatorExpiresAt, session.ExpiresAt);
        var production = server.Record(batch, InspectionPurpose.Production) with { ExecutionSessionId = session.Id, ProductionSequence = 1, StartedAt = session.IssuedAt, CompletedAt = session.IssuedAt.AddMilliseconds(1) };
        foreach (var invalid in new[]
        {
            production with { Id = Guid.NewGuid(), RecipeId = Guid.NewGuid().ToString() },
            production with { Id = Guid.NewGuid(), BatchId = Guid.NewGuid() },
            production with { Id = Guid.NewGuid(), RecipeJson = "{}" },
            production with { Id = Guid.NewGuid(), SampleId = "foreign-sample" },
            production with { Id = Guid.NewGuid(), SourceKind = "UnmarkedNormal" },
            production with { Id = Guid.NewGuid(), ExecutionSessionId = Guid.NewGuid() },
            production with { Id = Guid.NewGuid(), OperatorName = "another operator" },
            production with { Id = Guid.NewGuid(), ProductionSequence = 3 },
            production with { Id = Guid.NewGuid(), StartedAt = session.ExpiresAt.AddSeconds(1), CompletedAt = session.ExpiresAt.AddSeconds(2) }
        }) await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{invalid.Id}", invalid), HttpStatusCode.Conflict);
        await server.Status(server.OtherDevice.PutAsJsonAsync($"/api/inspections/{production.Id}", production), HttpStatusCode.Forbidden);
        var competing = production with { Id = Guid.NewGuid() };
        var uploaded = await Task.WhenAll(new[] { production, competing }.Select(record => server.Device.PutAsJsonAsync($"/api/inspections/{record.Id}", record)));
        Assert.Single(uploaded, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(uploaded, response => response.StatusCode == HttpStatusCode.Conflict);
        var receipt = (await uploaded.Single(response => response.IsSuccessStatusCode).Content.ReadFromJsonAsync<InspectionReceipt>())!;
        foreach (var response in uploaded) response.Dispose();
        var saved = receipt.InspectionId == production.Id ? production : competing;
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (session.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds + 150)));
        await server.LoginDevice();
        var delayed = production with { Id = Guid.NewGuid(), ProductionSequence = 2, ExecutionStatus = InspectionExecution.Failed,
            Decision = QualityDecision.NotEvaluated, TestedImage = null, ReferenceImage = null, Error = "isolated acquisition failure" };
        await server.Upload(delayed);
        await server.Login(server.Quality, "quality");
        var details = (await server.Quality.GetFromJsonAsync<BatchDetails>($"/api/batches/{batch.Batch.Id}"))!;
        Assert.Equal(2, details.ReceivedProductionCount);
        Assert.Equal(1, details.TechnicalFailureCount);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{saved.Id}", saved), HttpStatusCode.OK);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{saved.Id}", saved with { ProductId = "changed" }), HttpStatusCode.Conflict);
        await server.Execute("UPDATE Batches SET Status='Closed'");
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{saved.Id}", saved), HttpStatusCode.OK);
    }

    [Fact]
    public async Task ClosedBatchStillAcceptsPreviouslyStartedProductionOnFirstUpload()
    {
        await using var server = await BatchServer.CreateAsync();
        var batch = await server.CreateBatch();
        var first = server.Record(batch, InspectionPurpose.FirstArticle);
        await server.Upload(first);
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval",
            new ApproveFirstArticleRequest(first.Id)), HttpStatusCode.Created);
        using var start = await server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions",
            new StartBatchRequest("STATION-A", server.Version.BundleHash, server.ArchiveId));
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var session = (await start.Content.ReadFromJsonAsync<BatchExecutionSession>())!;
        var pending = server.Record(batch, InspectionPurpose.Production) with
        {
            ExecutionSessionId = session.Id, ProductionSequence = 1,
            StartedAt = session.IssuedAt, CompletedAt = session.IssuedAt.AddMilliseconds(1)
        };
        await server.Execute("UPDATE Batches SET Status='Closed'");
        await server.Upload(pending);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{pending.Id}", pending), HttpStatusCode.OK);
        var invalid = pending with { Id = Guid.NewGuid(), StartedAt = session.ExpiresAt.AddSeconds(1), CompletedAt = session.ExpiresAt.AddSeconds(2) };
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{invalid.Id}", invalid), HttpStatusCode.Conflict);
        var postApprovalFirst = server.Record(batch, InspectionPurpose.FirstArticle) with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(1), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{postApprovalFirst.Id}", postApprovalFirst), HttpStatusCode.Conflict);
        var detail = (await server.Quality.GetFromJsonAsync<BatchDetails>($"/api/batches/{batch.Batch.Id}"))!;
        Assert.Equal(1, detail.ReceivedProductionCount);
    }

    private sealed class BatchServer : IAsyncDisposable
    {
        private readonly string database = "BoardTrace_Batch_" + Guid.NewGuid().ToString("N");
        private WebApplicationFactory<Program> factory = null!;
        public HttpClient Engineer = null!, Quality = null!, Operator = null!, Device = null!, OtherDevice = null!;
        public PublishedRecipeVersion Version = null!;
        public Guid ArchiveId { get; } = Guid.NewGuid();
        public DateTimeOffset OperatorExpiresAt;
        private const string Master = "Server=(localdb)\\BoardTrace;Database=master;Integrated Security=true;TrustServerCertificate=true";
        private string Connection => $"Server=(localdb)\\BoardTrace;Database={database};Integrated Security=true;TrustServerCertificate=true";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static async Task<BatchServer> CreateAsync(TimeSpan? cookieLifetime = null)
        {
            var server = new BatchServer();
            await using (var connection = new SqlConnection(Master))
            {
                await connection.OpenAsync(); await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{server.database}]"; await command.ExecuteNonQueryAsync();
            }
            try
            {
                server.factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:BoardTrace"] = server.Connection }));
                    if (cookieLifetime is not null) builder.ConfigureServices(services => services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.ExpireTimeSpan = cookieLifetime.Value));
                });
                server.Engineer = server.factory.CreateClient();
                using var scope = server.factory.Services.CreateScope();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<BoardTraceUser>>();
                foreach (var role in new[] { "ProcessEngineer", "QualityEngineer", "Operator", "Station" }) Assert.True((await roles.CreateAsync(new(role))).Succeeded);
                foreach (var (name, role, station) in new[] { ("engineer", "ProcessEngineer", (string?)null), ("quality", "QualityEngineer", (string?)null), ("operator", "Operator", (string?)null), ("operator-2", "Operator", (string?)null), ("station-a", "Station", "STATION-A"), ("station-b", "Station", "STATION-B") })
                {
                    var user = new BoardTraceUser { Id = name, UserName = name, DisplayName = name, StationId = station };
                    Assert.True((await users.CreateAsync(user, "Test!Batch123")).Succeeded); Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                }
                var db = scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>();
                var draftId = Guid.NewGuid(); var runId = Guid.NewGuid(); var versionId = Guid.NewGuid(); var assetId = Guid.NewGuid();
                var settings = new RecipeClassicalSettings(); var targets = new RecipeTargets(1, 1, 500);
                var bundle = new PublishedRecipeBundle(versionId, draftId, runId, "ISOLATED BATCH FIXTURE", "Classical", settings, targets, targets,
                    new(640, 640, true), new string('a', 64), new string('b', 64), new string('c', 64),
                    [new("sample", assetId, Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), 3)], "engineer", "engineer", DateTimeOffset.UtcNow);
                server.Version = new(bundle, PublishedRecipeTransfer.Hash(bundle));
                db.RecipeDrafts.Add(new RecipeDraft { Id = draftId, Name = bundle.Name, SettingsJson = JsonSerializer.Serialize(settings), TargetsJson = JsonSerializer.Serialize(targets), DataManifestSha256 = bundle.InputManifestSha256, SnapshotHash = bundle.ValidationSnapshotHash, AuthorId = "engineer", UpdatedAt = DateTimeOffset.UtcNow });
                db.ValidationRuns.Add(new ValidationRun { Id = runId, DraftId = draftId, Name = bundle.Name, Status = "Completed", SettingsJson = JsonSerializer.Serialize(settings), TargetsJson = JsonSerializer.Serialize(targets), ManifestHash = bundle.InputManifestSha256, TruthHash = new string('d', 64), SnapshotHash = bundle.ValidationSnapshotHash, CreatedAt = DateTimeOffset.UtcNow });
                db.RecipeVersions.Add(new RecipePublication { Id = versionId, DraftId = draftId, ValidationRunId = runId, Name = bundle.Name, BundleJson = JsonSerializer.Serialize(bundle, Json), BundleHash = server.Version.BundleHash, PublishedById = "engineer", PublishedByName = "engineer", PublishedAt = bundle.PublishedAt, Assets = [new RecipeReferenceAsset { Id = assetId, RecipeVersionId = versionId, Sha256 = bundle.References[0].Sha256, Content = [1, 2, 3] }] });
                await db.SaveChangesAsync();
                server.Quality = server.factory.CreateClient(); server.Operator = server.factory.CreateClient(); server.Device = server.factory.CreateClient(); server.OtherDevice = server.factory.CreateClient();
                await server.Login(server.Engineer, "engineer"); await server.Login(server.Quality, "quality");
                await server.Login(server.Operator, "operator"); await server.LoginDevice(); await server.Login(server.OtherDevice, "station-b");
                return server;
            }
            catch { await server.DisposeAsync(); throw; }
        }

        public async Task Login(HttpClient client, string user)
        {
            using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, "Test!Batch123"));
            response.EnsureSuccessStatusCode();
            if (user == "operator")
            {
                var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".AspNetCore.Identity.Application="));
                var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
                OperatorExpiresAt = options.TicketDataFormat.Unprotect(cookie.Split(';')[0].Split('=', 2)[1])!.Properties.ExpiresUtc!.Value;
            }
        }
        public Task LoginDevice() => Login(Device, "station-a");
        public CreateBatchRequest Request() => new("ISOLATED-BATCH", "PCB", "TOP", 2, "STATION-A", Version.Bundle.VersionId);
        public async Task<BatchDetails> CreateBatch() { using var response = await Engineer.PostAsJsonAsync("/api/batches", Request()); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await response.Content.ReadFromJsonAsync<BatchDetails>())!; }
        public InspectionRecord Record(BatchDetails batch, InspectionPurpose purpose) => new()
        {
            Id = Guid.NewGuid(), Purpose = purpose, BatchId = batch.Batch.Id, StationId = "STATION-A", ProductId = "SIMULATED-PRODUCT",
            OperatorId = "operator", OperatorName = "operator", SampleId = "sample", SourceKind = "ConstructedNormal", RecipeId = Version.Bundle.VersionId.ToString("D"), RecipeJson = JsonSerializer.Serialize(Version, Json),
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow.AddMilliseconds(1), ExecutionStatus = InspectionExecution.Completed, Decision = QualityDecision.Pass, Width = 640, Height = 640, TestedImage = [1, 2, 3], ReferenceImage = [1, 2, 3]
        };
        public Task Upload(InspectionRecord record) => Status(Device.PutAsJsonAsync($"/api/inspections/{record.Id}", record), HttpStatusCode.Created);
        public async Task Status(Task<HttpResponseMessage> task, HttpStatusCode status) { using var response = await task; Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); }
        public async Task Execute(string sql) { await using var connection = new SqlConnection(Connection); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        public async ValueTask DisposeAsync()
        {
            Engineer?.Dispose(); Quality?.Dispose(); Operator?.Dispose(); Device?.Dispose(); OtherDevice?.Dispose(); factory?.Dispose();
            if (!database.StartsWith("BoardTrace_Batch_", StringComparison.Ordinal) || database.Length != "BoardTrace_Batch_".Length + 32 || !database.AsSpan("BoardTrace_Batch_".Length).ToString().All(Uri.IsHexDigit)) throw new InvalidOperationException("Unsafe database cleanup.");
            await using var connection = new SqlConnection(Master); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]"; await command.ExecuteNonQueryAsync();
        }
    }
}
