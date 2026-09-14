using System.Security.Claims;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Recipes;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Batches;

public static class BatchEndpoints
{
    public static void MapBatches(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stations", Stations).RequireAuthorization("Human");
        app.MapPost("/api/batches", Create).RequireAuthorization(policy => policy.RequireRole("ProcessEngineer"));
        app.MapGet("/api/batches", List).RequireAuthorization("Human");
        app.MapGet("/api/batches/{id:guid}", Detail).RequireAuthorization("Human");
        app.MapGet("/api/station/batches", StationList).RequireAuthorization("Station");
        app.MapGet("/api/station/batches/{id:guid}/package", Package).RequireAuthorization("Station");
        app.MapPut("/api/batches/{id:guid}/first-article-approval", Approve).RequireAuthorization(policy => policy.RequireRole("QualityEngineer"));
        app.MapPost("/api/batches/{id:guid}/execution-sessions", Start).RequireAuthorization(policy => policy.RequireRole("Operator"));
    }

    private static async Task<IResult> Stations(UserManager<BoardTraceUser> users) => Results.Ok(
        (await users.GetUsersInRoleAsync("Station")).Where(user => !string.IsNullOrWhiteSpace(user.StationId))
        .OrderBy(user => user.StationId).Select(user => new StationSummary(user.StationId!, user.DisplayName)).ToArray());

    private static async Task<IResult> Create(CreateBatchRequest request, ClaimsPrincipal principal,
        UserManager<BoardTraceUser> users, BoardTraceDbContext db, CancellationToken token)
    {
        if (new[] { request.BatchNumber, request.ProductType, request.FieldOfView, request.StationId }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128) || request.PlannedQuantity < 1)
            return Results.Problem(statusCode: 400, title: "批号、产品、视野、工位必填且最多128字，计划数量必须为正整数。");
        var existing = await db.Batches.AsNoTracking().SingleOrDefaultAsync(batch => batch.BatchNumber == request.BatchNumber, token);
        if (existing is not null) return existing.Matches(request) ? Results.Ok(await ReadDetails(existing, db, token)) : Conflict("批号已被不同建批内容使用。");
        if (!(await users.GetUsersInRoleAsync("Station")).Any(user => user.StationId == request.StationId)) return Conflict("指定工位不存在。");
        var version = await db.RecipeVersions.AsNoTracking().SingleOrDefaultAsync(version => version.Id == request.RecipeVersionId, token);
        if (version is null) return Conflict("只能使用已发布的不可变方案版本建批。");
        if (!await Available(version, db, token)) return Results.Problem(statusCode: 503, title: "已发布方案包或参考资产不可用。");
        var author = await users.GetUserAsync(principal);
        if (author is null) return Results.Unauthorized();
        var batch = new BatchEntity { Id = Guid.NewGuid(), BatchNumber = request.BatchNumber, ProductType = request.ProductType,
            FieldOfView = request.FieldOfView, PlannedQuantity = request.PlannedQuantity, StationId = request.StationId,
            RecipeVersionId = version.Id, RecipeBundleHash = version.BundleHash, CreatedById = author.Id,
            CreatedByName = author.DisplayName, CreatedAt = DateTimeOffset.UtcNow, Status = BatchStatus.AwaitingFirstArticle };
        db.Batches.Add(batch);
        try { await db.SaveChangesAsync(token); return Results.Created($"/api/batches/{batch.Id}", await ReadDetails(batch, db, token)); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            existing = await db.Batches.AsNoTracking().SingleOrDefaultAsync(value => value.BatchNumber == request.BatchNumber, token);
            return existing is not null && existing.Matches(request) ? Results.Ok(await ReadDetails(existing, db, token)) : Conflict("批号已使用或工位已有未关闭批次。");
        }
    }

    private static async Task<IResult> List(string? stationId, BatchStatus? status, BoardTraceDbContext db, CancellationToken token)
    {
        if (status is not null && !Enum.IsDefined(status.Value)) return Results.Problem(statusCode: 400, title: "批次状态无效。");
        var query = db.Batches.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(stationId)) query = query.Where(batch => batch.StationId == stationId);
        if (status is not null) query = query.Where(batch => batch.Status == status);
        return Results.Ok(await Summaries(query, db, token));
    }

    private static async Task<IResult> StationList(ClaimsPrincipal principal, UserManager<BoardTraceUser> users, BoardTraceDbContext db, CancellationToken token)
    {
        var station = await users.GetUserAsync(principal);
        if (station?.StationId is null) return Results.Forbid();
        return Results.Ok(await Summaries(db.Batches.AsNoTracking().Where(batch => batch.StationId == station.StationId), db, token));
    }

    private static async Task<BatchSummary[]> Summaries(IQueryable<BatchEntity> query, BoardTraceDbContext db, CancellationToken token)
    {
        var batches = await query.OrderByDescending(batch => batch.CreatedAt).ToArrayAsync(token);
        var ids = batches.Select(batch => batch.Id).ToArray();
        var counts = await db.Inspections.Where(row => row.BatchId != null && ids.Contains(row.BatchId.Value) && row.Purpose == InspectionPurpose.Production)
            .GroupBy(row => row.BatchId!.Value).Select(group => new { Id = group.Key, Count = group.Count() }).ToDictionaryAsync(row => row.Id, row => row.Count, token);
        return batches.Select(batch => new BatchSummary(batch.Definition(), batch.Status, counts.GetValueOrDefault(batch.Id))).ToArray();
    }

    private static async Task<IResult> Detail(Guid id, BoardTraceDbContext db, CancellationToken token)
    {
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(batch => batch.Id == id, token);
        return batch is null ? Results.NotFound() : Results.Ok(await ReadDetails(batch, db, token));
    }

    private static async Task<BatchDetails> ReadDetails(BatchEntity batch, BoardTraceDbContext db, CancellationToken token)
    {
        var approval = await db.FirstArticleApprovals.AsNoTracking().SingleOrDefaultAsync(value => value.BatchId == batch.Id, token);
        var firstArticles = await db.Inspections.AsNoTracking().Where(row => row.BatchId == batch.Id && row.Purpose == InspectionPurpose.FirstArticle)
            .OrderByDescending(row => row.StartedAt).Select(row => new BatchInspectionSummary(row.Id, row.ProductId, row.SourceKind,
                row.OperatorName, row.ExecutionStatus, row.Decision, row.StartedAt)).ToArrayAsync(token);
        var production = db.Inspections.Where(row => row.BatchId == batch.Id && row.Purpose == InspectionPurpose.Production);
        return new(batch.Definition(), batch.Status, approval, firstArticles, await production.CountAsync(token),
            await production.CountAsync(row => row.ExecutionStatus != InspectionExecution.Completed, token));
    }

    private static async Task<IResult> Package(Guid id, ClaimsPrincipal principal, UserManager<BoardTraceUser> users, BoardTraceDbContext db, CancellationToken token)
    {
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(batch => batch.Id == id, token);
        if (batch is null) return Results.NotFound();
        if ((await users.GetUserAsync(principal))?.StationId != batch.StationId) return Results.Forbid();
        var version = await db.RecipeVersions.AsNoTracking().SingleAsync(version => version.Id == batch.RecipeVersionId, token);
        if (version.BundleHash != batch.RecipeBundleHash || !await Available(version, db, token)) return Results.Problem(statusCode: 503, title: "批次固定版本包或参考资产不可用。");
        var approval = await db.FirstArticleApprovals.AsNoTracking().SingleOrDefaultAsync(approval => approval.BatchId == id, token);
        return Results.Ok(new BatchPackage(batch.Definition(), batch.Status, approval, version.View()));
    }

    private static async Task<bool> Available(RecipePublication publication, BoardTraceDbContext db, CancellationToken token)
    {
        var version = publication.View();
        if (version.BundleHash != PublishedRecipeTransfer.Hash(version.Bundle)) return false;
        // Batch refresh checks the immutable manifest against SQL metadata (Length becomes DATALENGTH).
        // Full bytes are hashed at publication and when the station saves/loads the downloaded assets.
        var assets = await db.RecipeAssets.AsNoTracking().Where(asset => asset.RecipeVersionId == publication.Id)
            .Select(asset => new { asset.Id, asset.Sha256, ByteLength = asset.Content.Length }).ToDictionaryAsync(asset => asset.Id, token);
        var model = version.Bundle.Model;
        if (version.Bundle.Definition is PairedOnnxRecipeDefinition paired)
        {
            if (model is null || model.Sha256 != paired.ModelSha256 || model.InputContract != RecipeModelInput.PairedGrayAbsDiff640V1 ||
                !assets.TryGetValue(model.AssetId, out var modelAsset) || modelAsset.ByteLength != model.ByteLength ||
                modelAsset.Sha256 != model.Sha256) return false;
        }
        else if (version.Bundle.Definition is not ClassicalRecipeDefinition || model is not null) return false;
        return version.Bundle.References.Count > 0 && version.Bundle.References.All(reference => assets.TryGetValue(reference.AssetId, out var asset) &&
            asset.ByteLength == reference.ByteLength && asset.Sha256 == reference.Sha256);
    }

    private static async Task<IResult> Approve(Guid id, ApproveFirstArticleRequest request, ClaimsPrincipal principal,
        UserManager<BoardTraceUser> users, BoardTraceDbContext db, CancellationToken token)
    {
        var existing = await db.FirstArticleApprovals.AsNoTracking().SingleOrDefaultAsync(approval => approval.BatchId == id, token);
        if (existing is not null) return ApprovalResult(existing, request.InspectionId);
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(batch => batch.Id == id, token);
        if (batch is null) return Results.NotFound();
        var inspection = await db.Inspections.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.InspectionId, token);
        var imageLengths = await db.Images.Where(image => image.InspectionId == request.InspectionId && (image.Kind == "tested" || image.Kind == "reference"))
            .Select(image => image.Content.Length).ToArrayAsync(token);
        if (inspection is null || inspection.BatchId != id || inspection.StationId != batch.StationId || inspection.RecipeId != batch.RecipeVersionId.ToString("D") ||
            inspection.Purpose != InspectionPurpose.FirstArticle || inspection.ExecutionStatus != InspectionExecution.Completed || inspection.Decision != QualityDecision.Pass ||
            imageLengths.Length != 2 || imageLengths.Any(length => length == 0))
            return Conflict("批准需要中央已有本批、本工位、固定版本的已完成 Pass 首件及两张原图。");
        var user = await users.GetUserAsync(principal);
        if (user is null) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await db.Batches.Where(batch => batch.Id == id && batch.Status == BatchStatus.AwaitingFirstArticle).ExecuteUpdateAsync(update => update.SetProperty(batch => batch.Status, BatchStatus.Approved), token) != 1)
        {
            await transaction.RollbackAsync(token);
            existing = await db.FirstArticleApprovals.AsNoTracking().SingleOrDefaultAsync(approval => approval.BatchId == id, token);
            return existing is null ? Conflict("批次当前状态不允许批准首件。") : ApprovalResult(existing, request.InspectionId);
        }
        var approval = new FirstArticleApproval(id, request.InspectionId, user.Id, user.DisplayName, DateTimeOffset.UtcNow);
        db.FirstArticleApprovals.Add(approval);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Results.Created($"/api/batches/{id}", approval);
    }

    private static IResult ApprovalResult(FirstArticleApproval approval, Guid inspectionId) => approval.InspectionId == inspectionId
        ? Results.Ok(approval) : Conflict("本批已批准其他首件，不能覆盖原批准。");

    private static async Task<IResult> Start(Guid id, StartBatchRequest request, HttpContext context,
        UserManager<BoardTraceUser> users, BoardTraceDbContext db, CancellationToken token)
    {
        if (request.ArchiveId == Guid.Empty) return Results.BadRequest("本地执行档案身份不能为空。");
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(batch => batch.Id == id, token);
        if (batch is null) return Results.NotFound();
        if (batch.StationId != request.StationId || batch.RecipeBundleHash != request.RecipeBundleHash) return Conflict("工位或方案包不属于该批次。");
        var approval = await db.FirstArticleApprovals.AsNoTracking().SingleOrDefaultAsync(approval => approval.BatchId == id, token);
        if (approval is null) return Conflict("批次首件尚未批准，不能启动生产。");
        var user = await users.GetUserAsync(context.User);
        var authentication = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var now = DateTimeOffset.UtcNow;
        if (user is null || authentication.Properties?.ExpiresUtc is not DateTimeOffset expiresAt || expiresAt <= now) return Results.Unauthorized();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Serialize session issuance on the batch row, including idempotent retries.
        if (await db.Batches.Where(batch => batch.Id == id && (batch.Status == BatchStatus.Approved || batch.Status == BatchStatus.InProgress))
            .ExecuteUpdateAsync(update => update.SetProperty(batch => batch.Status, BatchStatus.InProgress), token) != 1)
            return Conflict("批次当前状态不允许启动生产。");
        var archiveId = await db.Batches.Where(batch => batch.Id == id).Select(batch => batch.ArchiveId).SingleAsync(token);
        if (archiveId is not null && archiveId != request.ArchiveId)
            return Conflict("该批次已绑定另一份执行档案，请恢复原工位 SQLite 档案后继续；不能从空库重新接件。");
        if (archiveId is null)
            await db.Batches.Where(batch => batch.Id == id).ExecuteUpdateAsync(update => update.SetProperty(batch => batch.ArchiveId, request.ArchiveId), token);
        var existing = await db.BatchExecutionSessions.AsNoTracking().Where(session => session.BatchId == id && session.OperatorId == user.Id && session.ExpiresAt > now)
            .OrderByDescending(session => session.IssuedAt).FirstOrDefaultAsync(token);
        if (existing is not null) { await transaction.CommitAsync(token); return Results.Ok(existing); }
        var session = new BatchExecutionSession(Guid.NewGuid(), id, batch.StationId, batch.RecipeBundleHash,
            approval.InspectionId, user.Id, user.DisplayName, now, expiresAt, request.ArchiveId);
        db.BatchExecutionSessions.Add(session);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Results.Created($"/api/batches/{id}", session);
    }

    private static IResult Conflict(string reason) => Results.Problem(statusCode: 409, title: reason);
}
