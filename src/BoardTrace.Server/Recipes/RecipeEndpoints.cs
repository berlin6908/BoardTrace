using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Recipes;

public static class RecipeEndpoints
{
    public static void MapRecipes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recipes");
        group.MapPost("/drafts", Create).RequireAuthorization("Human");
        group.MapPut("/drafts/{id:guid}", Update).RequireAuthorization("Human");
        group.MapGet("/drafts", List).RequireAuthorization("Human");
        group.MapGet("/drafts/{id:guid}", Detail).RequireAuthorization("Human");
        group.MapPost("/drafts/{id:guid}/validations", Validate).RequireAuthorization("Human");
        group.MapGet("/drafts/{id:guid}/validations", ListValidations).RequireAuthorization("Human");
        group.MapGet("/validations/{id:guid}", ValidationDetail).RequireAuthorization("Human");
    }

    private static bool Engineer(ClaimsPrincipal user) => user.IsInRole("ProcessEngineer");

    private static string? Check(SaveRecipeDraft input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200) return "方案名称不能为空且不能超过 200 字。";
        var t = input.Targets;
        if (input.Definition is null || t is null) return "检测方案和验收目标必填。";
        if (input.Definition is ClassicalRecipeDefinition classical)
        {
            var s = classical.Settings;
            if (s is null || s.BinarizationThreshold is < 0 or > 255 || s.EdgeTolerance is < 0 or > 10 ||
                s.MinimumArea < 1 || s.ClosingSize < 1 || s.ClosingSize > 31 || s.ClosingSize % 2 == 0 ||
                s.BoxPadding is < 0 or > 100 || !double.IsFinite(s.MaximumTranslation) || s.MaximumTranslation < 0 ||
                !double.IsFinite(s.MinimumAlignmentResponse) || s.MinimumAlignmentResponse is < 0 or > 1)
                return "经典检测参数超出有效范围。";
        }
        else if (input.Definition is PairedOnnxRecipeDefinition paired)
        {
            if (!RecipeModelEndpoints.IsSha256(paired.ModelSha256)) return "模型 SHA-256 无效。";
            if (paired.Thresholds is not { } s || new[] { s.Open, s.Short, s.Mousebite, s.Spur, s.Copper, s.PinHole }
                .Any(value => !double.IsFinite(value) || value is < 0 or > 1)) return "六类置信度阈值必须完整且在 0–1 范围。";
        }
        else return "检测算法不支持。";
        if (!double.IsFinite(t.MinPrecision) || t.MinPrecision is < 0 or > 1 ||
            !double.IsFinite(t.MinRecall) || t.MinRecall is < 0 or > 1 ||
            !double.IsFinite(t.MaxP95Ms) || t.MaxP95Ms <= 0)
            return "验收目标超出有效范围。";
        return null;
    }

    private static async Task<string?> ManifestHash(IConfiguration config, CancellationToken token)
    {
        var root = config["RecipeValidation:ManifestRoot"];
        var path = root is null ? null : Path.Combine(root, "inputs", "validation.jsonl");
        return path is null || !File.Exists(path) ? null :
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, token)));
    }

    private static async Task<IResult> Create(SaveRecipeDraft input, ClaimsPrincipal user, BoardTraceDbContext db,
        IConfiguration config, CancellationToken token)
    {
        if (!Engineer(user)) return Results.Forbid();
        if (Check(input) is string error) return Results.Problem(statusCode: 400, title: error);
        if (input.Definition is PairedOnnxRecipeDefinition paired)
        {
            paired = paired with { ModelSha256 = paired.ModelSha256.ToLowerInvariant() };
            if (!await db.RecipeModels.AnyAsync(x => x.Sha256 == paired.ModelSha256, token))
                return Results.Problem(statusCode: 409, title: "模型尚未上传。");
            input = input with { Definition = paired };
        }
        var manifestHash = await ManifestHash(config, token);
        if (manifestHash is null) return Results.Problem(statusCode: 503, title: "固定验证清单未配置。");
        var settings = JsonSerializer.Serialize<RecipeDefinition>(input.Definition);
        var targets = JsonSerializer.Serialize(input.Targets);
        var draft = new RecipeDraft { Id = Guid.NewGuid(), Name = input.Name.Trim(), DefinitionJson = settings,
            TargetsJson = targets, DataManifestSha256 = manifestHash,
            SnapshotHash = RecipeDraft.Hash(input.Name.Trim(), settings, targets, manifestHash),
            AuthorId = user.FindFirstValue(ClaimTypes.NameIdentifier)!, UpdatedAt = DateTimeOffset.UtcNow };
        db.RecipeDrafts.Add(draft);
        await db.SaveChangesAsync(token);
        return Results.Created($"/api/recipes/drafts/{draft.Id}", draft.View());
    }

    private static async Task<IResult> Update(Guid id, SaveRecipeDraft input, ClaimsPrincipal user,
        BoardTraceDbContext db, IConfiguration config, CancellationToken token)
    {
        if (!Engineer(user)) return Results.Forbid();
        if (Check(input) is string error) return Results.Problem(statusCode: 400, title: error);
        if (input.Definition is PairedOnnxRecipeDefinition paired)
        {
            paired = paired with { ModelSha256 = paired.ModelSha256.ToLowerInvariant() };
            if (!await db.RecipeModels.AnyAsync(x => x.Sha256 == paired.ModelSha256, token))
                return Results.Problem(statusCode: 409, title: "模型尚未上传。");
            input = input with { Definition = paired };
        }
        var manifestHash = await ManifestHash(config, token);
        if (manifestHash is null) return Results.Problem(statusCode: 503, title: "固定验证清单未配置。");
        var draft = await db.RecipeDrafts.FindAsync([id], token);
        if (draft is null) return Results.NotFound();
        draft.Name = input.Name.Trim();
        draft.DefinitionJson = JsonSerializer.Serialize<RecipeDefinition>(input.Definition);
        draft.TargetsJson = JsonSerializer.Serialize(input.Targets);
        draft.DataManifestSha256 = manifestHash;
        draft.SnapshotHash = RecipeDraft.Hash(draft.Name, draft.DefinitionJson, draft.TargetsJson, manifestHash);
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        return Results.Ok(draft.View());
    }

    private static async Task<IResult> List(BoardTraceDbContext db, CancellationToken token) =>
        Results.Ok((await db.RecipeDrafts.AsNoTracking().OrderByDescending(x => x.UpdatedAt).ToListAsync(token))
            .Select(x => x.View()));

    private static async Task<IResult> Detail(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        var draft = await db.RecipeDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return draft is null ? Results.NotFound() : Results.Ok(draft.View());
    }

    private static async Task<IResult> Validate(Guid id, ClaimsPrincipal user, BoardTraceDbContext db,
        IConfiguration config, CancellationToken token)
    {
        if (!Engineer(user)) return Results.Forbid();
        var draft = await db.RecipeDrafts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (draft is null) return Results.NotFound();
        var root = config["RecipeValidation:ManifestRoot"];
        if (string.IsNullOrWhiteSpace(root)) return Results.Problem(statusCode: 503, title: "验证清单目录未配置。");
        var inputPath = Path.Combine(root, "inputs", "validation.jsonl");
        var truthPath = Path.Combine(root, "truth", "validation.jsonl");
        if (!File.Exists(inputPath) || !File.Exists(truthPath))
            return Results.Problem(statusCode: 503, title: "固定验证清单缺失。");
        var total = File.ReadLines(inputPath).Count();
        if (total != 200 || File.ReadLines(truthPath).Count() != 200)
            return Results.Problem(statusCode: 503, title: "固定验证人口必须为 200 张。");
        var currentManifestHash = await ManifestHash(config, token);
        if (draft.DataManifestSha256 != currentManifestHash)
            return Results.Problem(statusCode: 409, title: "固定验证清单已改变",
                detail: "请重新保存草稿后再验证。");
        var run = new ValidationRun { Id = Guid.NewGuid(), DraftId = id, Status = "Queued", Name = draft.Name, Processed = 0,
            Total = total, SnapshotHash = draft.SnapshotHash, DefinitionJson = draft.DefinitionJson,
            TargetsJson = draft.TargetsJson,
            ManifestHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(inputPath, token))),
            TruthHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(truthPath, token))),
            CreatedAt = DateTimeOffset.UtcNow };
        db.ValidationRuns.Add(run);
        await db.SaveChangesAsync(token);
        return Results.Accepted($"/api/recipes/validations/{run.Id}", run.View());
    }

    private static async Task<IResult> ListValidations(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        if (!await db.RecipeDrafts.AnyAsync(x => x.Id == id, token)) return Results.NotFound();
        return Results.Ok((await db.ValidationRuns.AsNoTracking().Where(x => x.DraftId == id)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(token)).Select(x => x.View()));
    }

    private static async Task<IResult> ValidationDetail(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        var run = await db.ValidationRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return run is null ? Results.NotFound() : Results.Ok(run.View());
    }
}
