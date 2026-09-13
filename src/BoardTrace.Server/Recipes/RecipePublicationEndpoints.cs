using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Storage;
using BoardTrace.Vision;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Recipes;

public static class RecipePublicationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string PublishedReaders = "Operator,ProcessEngineer,QualityEngineer,Station";

    public static void MapRecipePublications(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/recipes/drafts/{id:guid}/publish", Publish).RequireAuthorization("Human");
        app.MapGet("/api/recipes/release-policy", ReleasePolicy).RequireAuthorization("Human");
        app.MapGet("/api/recipes/versions", List).RequireAuthorization("Human");
        app.MapGet("/api/recipes/versions/{id:guid}", Detail).RequireAuthorization("Human");
        app.MapGet("/api/recipes/versions/{id:guid}/bundle", Detail)
            .RequireAuthorization(policy => policy.RequireRole(PublishedReaders.Split(',')));
        app.MapGet("/api/recipe-assets/{id:guid}", Reference)
            .RequireAuthorization(policy => policy.RequireRole(PublishedReaders.Split(',')));
    }

    private static async Task<IResult> Publish(Guid id, PublishRecipeRequest request, ClaimsPrincipal principal,
        UserManager<BoardTraceUser> users, BoardTraceDbContext db, IConfiguration config, CancellationToken token)
    {
        if (!principal.IsInRole("ProcessEngineer")) return Results.Forbid();
        var existing = await db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.ValidationRunId == request.ValidationRunId, token);
        if (existing is not null) return Existing(existing, id);
        var author = await users.GetUserAsync(principal);
        if (author is null) return Results.Unauthorized();
        var policy = await RecipePublicationPolicy.ReadAsync(config, token);
        if (!policy.IsFrozen) return Results.Problem(statusCode: 409, title: policy.Reason);

        // Hold the draft/run read locks until the version and BLOBs commit. A draft edit
        // cannot race between the eligibility check and publication.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        try
        {
            var draft = await db.RecipeDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (draft is null) return Results.NotFound();
            var run = await db.ValidationRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ValidationRunId && x.DraftId == id, token);
            if (run is null) return Results.Problem(statusCode: 404, title: "验证记录不属于该草稿或不存在。");
            if (run.SnapshotHash != draft.SnapshotHash)
                throw new PublicationRejected(409, "草稿已改变，请重新验证当前设置与目标。");
            if (run.Status != "Completed" || run.CompletedAt is null || run.Processed != 200 || run.Total != 200 || run.ReportJson is null)
                throw new PublicationRejected(409, "验证必须完成固定 200 张全部样本后才能发布。");
            var report = JsonSerializer.Deserialize<RecipeValidationReport>(run.ReportJson)!;
            var targets = JsonSerializer.Deserialize<RecipeTargets>(run.TargetsJson)!;
            if (report.Rows.Count != 200)
                throw new PublicationRejected(409, "验证报告必须完整包含 200 张样本。");
            if (report.ExecutionFailures != 0 || report.Rows.Any(row => row.Status != "Completed" || row.Error is not null))
                throw new PublicationRejected(409, "验证存在执行失败，不能发布。");
            if (!report.MeetsTargets || !MeetsTargets(report, targets))
                throw new PublicationRejected(409, "验证未达到本草稿的精确率、召回率或耗时目标。");
            if (!MeetsTargets(report, policy.Targets!))
                throw new PublicationRejected(409, "验证未达到服务端独立冻结目标，不能发布。");
            var algorithmHash = Hash(await File.ReadAllBytesAsync(typeof(ClassicalDetector).Assembly.Location, token));
            if (algorithmHash != report.AlgorithmAssemblySha256)
                throw new PublicationRejected(409, "当前算法程序集与验证时不同，请重新验证。");

            var manifestRoot = config["RecipeValidation:ManifestRoot"];
            var dataRoot = config["RecipeValidation:DataRoot"];
            if (string.IsNullOrWhiteSpace(manifestRoot) || string.IsNullOrWhiteSpace(dataRoot))
                throw new PublicationRejected(503, "固定验证清单或参考资产目录未配置。");
            var manifestBytes = await ReadFile(Path.Combine(manifestRoot, "inputs", "validation.jsonl"), "固定验证清单", token);
            if (Hash(manifestBytes) != run.ManifestHash || run.ManifestHash != draft.DataManifestSha256)
                throw new PublicationRejected(409, "固定验证清单已改变，请重新保存草稿并验证。");
            var inputs = Encoding.UTF8.GetString(manifestBytes).TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<ValidationReference>(line, Json)!).ToArray();
            var inputIds = inputs.Select(input => input.SampleId).ToHashSet(StringComparer.Ordinal);
            if (inputs.Length != 200 || inputIds.Count != 200 || report.Rows.Select(row => row.SampleId).Distinct(StringComparer.Ordinal).Count() != 200 ||
                !inputIds.SetEquals(report.Rows.Select(row => row.SampleId)))
                throw new PublicationRejected(409, "验证样本与固定 200 张清单不一致。");

            var versionId = Guid.NewGuid();
            var assets = new Dictionary<string, RecipeReferenceAsset>(StringComparer.Ordinal);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var references = new List<PublishedRecipeReference>(200);
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)) + Path.DirectorySeparatorChar;
            foreach (var input in inputs.OrderBy(input => input.SampleId, StringComparer.Ordinal))
            {
                var path = Path.GetFullPath(Path.Combine(fullRoot, input.Reference));
                if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    throw new PublicationRejected(409, "参考资产路径不属于配置的数据目录。");
                if (!files.TryGetValue(path, out var bytes))
                {
                    bytes = await ReadFile(path, $"参考资产（样本 {input.SampleId}）", token);
                    files.Add(path, bytes);
                }
                var hash = Hash(bytes);
                if (hash != input.ReferenceSha256)
                    throw new PublicationRejected(409, $"参考资产内容已改变（样本 {input.SampleId}），请重新验证。");
                if (!assets.TryGetValue(hash, out var asset))
                {
                    asset = new RecipeReferenceAsset { Id = Guid.NewGuid(), RecipeVersionId = versionId, Sha256 = hash, Content = bytes };
                    assets.Add(hash, asset);
                }
                references.Add(new(input.SampleId, asset.Id, hash, bytes.Length));
            }
            var publishedAt = DateTimeOffset.UtcNow;
            var bundle = new PublishedRecipeBundle(versionId, id, run.Id, run.Name, "Classical",
                JsonSerializer.Deserialize<RecipeClassicalSettings>(run.SettingsJson)!, targets, policy.Targets!,
                new PublishedRecipeInput(640, 640, true), algorithmHash, run.ManifestHash, run.SnapshotHash,
                references, author.Id, author.DisplayName, publishedAt);
            var publication = new RecipePublication
            {
                Id = versionId, DraftId = id, ValidationRunId = run.Id, Name = run.Name,
                BundleJson = JsonSerializer.Serialize(bundle, Json), BundleHash = PublishedRecipeTransfer.Hash(bundle),
                PublishedById = author.Id, PublishedByName = author.DisplayName, PublishedAt = publishedAt,
                Assets = assets.Values.ToList()
            };
            db.RecipeVersions.Add(publication);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Results.Created($"/api/recipes/versions/{versionId}", publication.View());
        }
        catch (PublicationRejected error)
        {
            return Results.Problem(statusCode: error.Status, title: error.Message);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 409, title: "验证报告或固定清单格式无效，不能发布。");
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            existing = await db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(x => x.ValidationRunId == request.ValidationRunId, token);
            if (existing is null) throw;
            return Existing(existing, id);
        }
    }

    private static IResult Existing(RecipePublication publication, Guid draftId) => publication.DraftId == draftId
        ? Results.Ok(publication.View()) : Results.Problem(statusCode: 409, title: "验证记录不属于该草稿。");

    private static bool MeetsTargets(RecipeValidationReport report, RecipeTargets targets) =>
        double.IsFinite(report.Precision) && double.IsFinite(report.Recall) && double.IsFinite(report.P95Ms) &&
        report.Precision >= targets.MinPrecision && report.Recall >= targets.MinRecall && report.P95Ms <= targets.MaxP95Ms;

    private static async Task<IResult> ReleasePolicy(IConfiguration config, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await RecipePublicationPolicy.ReadAsync(config, token));
    }

    private static async Task<IResult> List(BoardTraceDbContext db, CancellationToken token) => Results.Ok(
        await db.RecipeVersions.AsNoTracking().OrderByDescending(version => version.PublishedAt)
            .Select(version => new PublishedRecipeSummary(version.Id, version.DraftId, version.ValidationRunId,
                version.Name, "Classical", version.BundleHash, version.PublishedById, version.PublishedByName, version.PublishedAt)).ToArrayAsync(token));

    private static async Task<IResult> Detail(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        var version = await db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(version => version.Id == id, token);
        return version is null ? Results.NotFound() : Results.Ok(version.View());
    }

    private static async Task<IResult> Reference(Guid id, BoardTraceDbContext db, HttpContext context, CancellationToken token)
    {
        var asset = await db.RecipeReferenceAssets.AsNoTracking().SingleOrDefaultAsync(asset => asset.Id == id, token);
        if (asset is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "private,no-store";
        var bytes = asset.Content;
        var contentType = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png"
            : bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8 ? "image/jpeg" : "application/octet-stream";
        return Results.Bytes(bytes, contentType);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<byte[]> ReadFile(string path, string description, CancellationToken token)
    {
        try { return await File.ReadAllBytesAsync(path, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new PublicationRejected(503, $"{description}不可用，不能发布。"); }
    }

    private sealed record ValidationReference(string SampleId, string Reference, string ReferenceSha256);
    private sealed class PublicationRejected(int status, string message) : Exception(message)
    { public int Status { get; } = status; }
}
