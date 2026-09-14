using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Recipes;
using BoardTrace.Server.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardTrace.Server.Tests;

public sealed partial class PublicationTests
{
    private static SaveRecipeDraft PairedDraft(string hash, double open = .5) => new("isolated paired mechanism fixture",
        new PairedOnnxRecipeDefinition(hash, new(open, .5, .5, .5, .5, .5)), new(1, 1, 1000));

    [Fact]
    public async Task ModelUploadUsesRealGraphAndShaImmutableIdentityAndEngineerPermission()
    {
        await using var server = await PublicationServer.CreateAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "pixel-detector.onnx"));
        var hash = PublicationServer.Hash(bytes);
        foreach (var client in new[] { server.Operator, server.Quality, server.Station, server.Anonymous })
        {
            using var denied = await PublicationServer.Upload(client, bytes, hash);
            Assert.Equal(client == server.Anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var wrongHash = await PublicationServer.Upload(server.Engineer, bytes, new string('0', 64));
        Assert.Equal(HttpStatusCode.BadRequest, wrongHash.StatusCode);
        using var badGraph = await PublicationServer.Upload(server.Engineer, [1, 2, 3], PublicationServer.Hash([1, 2, 3]));
        Assert.Equal(HttpStatusCode.BadRequest, badGraph.StatusCode);
        using var wrongContract = await PublicationServer.Upload(server.Engineer, bytes, hash, "Rgb640");
        Assert.Equal(HttpStatusCode.BadRequest, wrongContract.StatusCode);
        using var oversized = new ByteArrayContent([]);
        oversized.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        oversized.Headers.ContentLength = RecipeModelEndpoints.MaximumBytes + 1L;
        using var rejectedSize = await server.Engineer.PostAsync($"/api/recipes/models?sha256={hash}&inputContract={RecipeModelInput.PairedGrayAbsDiff640V1}", oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejectedSize.StatusCode);
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PublicationServer.Upload(server.Engineer, bytes, hash)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        var summaries = new List<RecipeModelSummary>();
        foreach (var response in responses)
        {
            using (response)
            {
                response.EnsureSuccessStatusCode();
                summaries.Add((await response.Content.ReadFromJsonAsync<RecipeModelSummary>())!);
            }
        }
        Assert.All(summaries, item => Assert.Equal(summaries[0], item));
        Assert.Equal(hash, summaries[0].Sha256);
        Assert.Equal(bytes.Length, summaries[0].ByteLength);
        foreach (var client in new[] { server.Engineer, server.Quality, server.Operator })
            Assert.Equal(summaries[0], Assert.Single((await client.GetFromJsonAsync<RecipeModelSummary[]>("/api/recipes/models"))!));
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Station.GetAsync("/api/recipes/models")).StatusCode);
        using var missing = await server.Engineer.PostAsJsonAsync("/api/recipes/drafts", PairedDraft(new string('f', 64)));
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
    }

    [Fact]
    public async Task PairedValidationPublishesModelAndReferencesAtomicallyAndNeverNeedsSourceAssetsAgain()
    {
        await using var server = await PublicationServer.CreateAsync();
        await server.UsePairedTruthAsync();
        var modelBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "pixel-detector.onnx"));
        var hash = PublicationServer.Hash(modelBytes);
        using (var upload = await PublicationServer.Upload(server.Engineer, modelBytes, hash)) upload.EnsureSuccessStatusCode();
        var draft = await server.CreateDraftAsync(PairedDraft(hash));
        var run = await server.ValidateAsync(draft.Id);
        var report = run.Report!;
        Assert.Equal("ClassAware", report.MatchingMode);
        Assert.Equal(hash, report.ModelSha256);
        Assert.True(report.SessionInitializationMs > 0);
        Assert.Equal((800, 0, 0), (report.Tp, report.Fp, report.Fn));
        Assert.Equal(new[] { 200, 200, 0, 200, 200, 0 }, report.Classes.Select(x => x.Tp));
        await server.ReplaceReportAsync(run.Id, report with { MatchingMode = "Localization" });
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "匹配口径");
        await server.ReplaceReportAsync(run.Id, report with { ModelSha256 = new string('a', 64) });
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "模型身份");
        await server.ReplaceReportAsync(run.Id, report);
        using (var change = await server.Engineer.PutAsJsonAsync($"/api/recipes/drafts/{draft.Id}", PairedDraft(hash, .9))) change.EnsureSuccessStatusCode();
        await server.AssertRejectedAsync(draft.Id, run.Id, HttpStatusCode.Conflict, "草稿");
        using (var restore = await server.Engineer.PutAsJsonAsync($"/api/recipes/drafts/{draft.Id}", PairedDraft(hash))) restore.EnsureSuccessStatusCode();
        await server.SqlAsync("CREATE TRIGGER RejectPairedAssets ON RecipeAssets AFTER INSERT AS THROW 51001, 'fixture rejects asset transaction', 1;");
        using (var rejected = await server.Engineer.PostAsJsonAsync($"/api/recipes/drafts/{draft.Id}/publish", new PublishRecipeRequest(run.Id)))
            Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
        Assert.Equal(0, await server.CountVersionsAsync());
        Assert.Equal(0, await server.AssetCountAsync());
        await server.SqlAsync("DROP TRIGGER RejectPairedAssets;");
        using var published = await server.Engineer.PostAsJsonAsync($"/api/recipes/drafts/{draft.Id}/publish", new PublishRecipeRequest(run.Id));
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var version = (await published.Content.ReadFromJsonAsync<PublishedRecipeVersion>())!;
        Assert.Equal("PairedOnnx", version.Bundle.Definition.Algorithm);
        Assert.Equal(hash, version.Bundle.Model!.Sha256);
        Assert.Equal(3, await server.AssetCountAsync()); // Two reference images plus model.
        await server.AssignAsync(version.Bundle.VersionId);
        var originalBundle = await server.Station.GetByteArrayAsync($"/api/recipes/versions/{version.Bundle.VersionId}/bundle");
        await server.SqlAsync("DELETE FROM RecipeModels;");
        File.Delete(server.ReferencePath(0));
        File.Delete(server.ReferencePath(1));
        Assert.Equal(modelBytes, await server.Station.GetByteArrayAsync($"/api/recipe-assets/{version.Bundle.Model.AssetId}"));
        Assert.Equal(originalBundle, await server.Station.GetByteArrayAsync($"/api/recipes/versions/{version.Bundle.VersionId}/bundle"));
        Assert.Equal("PairedOnnx", Assert.Single((await server.Engineer.GetFromJsonAsync<PublishedRecipeSummary[]>("/api/recipes/versions"))!).Algorithm);
        // A new batch must refuse a broken full bundle, even when references still exist.
        await server.SqlAsync($"DELETE FROM RecipeAssets WHERE Id='{version.Bundle.Model.AssetId:D}';");
        using var missingModel = await server.Engineer.PostAsJsonAsync("/api/batches", new CreateBatchRequest("BROKEN-PAIRED", "PCB", "TOP", 2, "PUBLICATION-STATION", version.Bundle.VersionId));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missingModel.StatusCode);
    }

    [Fact]
    public async Task PairedFailedImageCountsEveryClassAndModelFailureDoesNotRunClassical()
    {
        await using var server = await PublicationServer.CreateAsync();
        await server.UsePairedTruthAsync();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "pixel-detector.onnx"));
        var hash = PublicationServer.Hash(bytes);
        using (var upload = await PublicationServer.Upload(server.Engineer, bytes, hash)) upload.EnsureSuccessStatusCode();
        var draft = await server.CreateDraftAsync(PairedDraft(hash));
        await server.CorruptFirstImageAsync();
        var failedImage = await server.RunAsync(draft.Id);
        Assert.Equal("Completed", failedImage.Status);
        Assert.Equal(200, failedImage.Processed);
        Assert.Equal(1, failedImage.Report!.ExecutionFailures);
        Assert.Equal(4, failedImage.Report.Fn);
        Assert.Equal(new[] { 1, 1, 0, 1, 1, 0 }, failedImage.Report.Classes.Select(x => x.Fn));
        Assert.Equal("NotEvaluated", failedImage.Report.Rows[0].Decision);
        await server.AssertRejectedAsync(draft.Id, failedImage.Id, HttpStatusCode.Conflict, "执行失败");
        await server.SqlAsync("DELETE FROM RecipeModels;");
        var absent = await server.RunAsync(draft.Id);
        Assert.Equal("Failed", absent.Status);
        Assert.Equal(0, absent.Processed);
        Assert.Null(absent.Report);
        Assert.Contains("模型资产缺失", absent.Error);
    }

    private sealed partial class PublicationServer
    {
        public static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes, string hash,
            string contract = RecipeModelInput.PairedGrayAbsDiff640V1)
        {
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return await client.PostAsync($"/api/recipes/models?sha256={hash}&inputContract={contract}", content);
        }

        public async Task UsePairedTruthAsync()
        {
            var inputs = (await File.ReadAllLinesAsync(InputManifestPath)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var truths = inputs.Select(input => JsonSerializer.Serialize(new { sampleId = input.RootElement.GetProperty("sampleId").GetString(),
                    defects = new[] { 1, 2, 4, 5 }.Select(id => new { classId = id, box = new[] { 10.25, 20.5, 30.25, 50.5 } }).ToArray() }));
                await File.WriteAllLinesAsync(Path.Combine(directory, "manifests", "truth", "validation.jsonl"), truths);
            }
            finally { foreach (var input in inputs) input.Dispose(); }
        }

        public Task CorruptFirstImageAsync() => File.WriteAllBytesAsync(Path.Combine(directory, "data", "generated-0-000.png"), [1, 2, 3]);

        public async Task<RecipeValidationView> RunAsync(Guid draft)
        {
            using var response = await Engineer.PostAsync($"/api/recipes/drafts/{draft}/validations", null);
            response.EnsureSuccessStatusCode();
            var run = (await response.Content.ReadFromJsonAsync<RecipeValidationView>())!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (run.Status is "Queued" or "Running")
            {
                await Task.Delay(100, timeout.Token);
                run = (await Engineer.GetFromJsonAsync<RecipeValidationView>($"/api/recipes/validations/{run.Id}", timeout.Token))!;
            }
            return run;
        }

        public async Task SqlAsync(string text)
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = text;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<int> AssetCountAsync()
        {
            using var scope = factory!.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>().RecipeAssets.CountAsync();
        }

    }
}
