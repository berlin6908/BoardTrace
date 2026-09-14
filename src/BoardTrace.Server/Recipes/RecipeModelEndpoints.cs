using System.Security.Cryptography;
using BoardTrace.Contracts;
using BoardTrace.Server.Storage;
using BoardTrace.Vision;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Recipes;

public static class RecipeModelEndpoints
{
    public const int MaximumBytes = 200 * 1024 * 1024;

    public static void MapRecipeModels(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/recipes/models", List).RequireAuthorization("Human");
        app.MapPost("/api/recipes/models", Upload).RequireAuthorization(policy => policy.RequireRole("ProcessEngineer"));
    }

    internal static bool IsSha256(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static async Task<IResult> List(BoardTraceDbContext db, CancellationToken token) => Results.Ok(
        await db.RecipeModels.AsNoTracking().OrderByDescending(x => x.CreatedAt)
            .Select(x => new RecipeModelSummary(x.Sha256, x.ByteLength, x.InputContract, x.CreatedAt)).ToArrayAsync(token));

    private static async Task<IResult> Upload(string sha256, string inputContract, HttpContext context,
        BoardTraceDbContext db, CancellationToken token)
    {
        if (!IsSha256(sha256) || inputContract != RecipeModelInput.PairedGrayAbsDiff640V1)
            return Results.Problem(statusCode: 400, title: "需要模型 SHA-256 和 PairedGrayAbsDiff640V1 输入声明。");
        if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return Results.Problem(statusCode: 415, title: "模型上传需要 application/octet-stream 原始字节。");
        // Set the Kestrel limit before reading any body. Keep the application's 12 MiB
        // inspection limit unchanged; the explicit loop also bounds chunked/TestServer input.
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaximumBytes;
        if (context.Request.ContentLength > MaximumBytes) return Results.StatusCode(413);
        using var stream = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(buffer, token);
            if (count == 0) break;
            if (stream.Length + count > MaximumBytes) return Results.StatusCode(413);
            await stream.WriteAsync(buffer.AsMemory(0, count), token);
        }
        var bytes = stream.ToArray();
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!actualHash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            return Results.Problem(statusCode: 400, title: "上传模型 SHA-256 与指定导出文件不一致。");
        var existing = await db.RecipeModels.AsNoTracking().SingleOrDefaultAsync(x => x.Sha256 == actualHash, token);
        if (existing is not null) return Results.Ok(existing.View());
        try
        {
            // Loads and checks the real graph contract, without image inference.
            using var detector = new OnnxDetector(bytes, actualHash);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException)
        {
            return Results.Problem(statusCode: 400, title: "模型不可加载或输入输出声明不符。", detail: error.Message);
        }
        var model = new RecipeModel { Sha256 = actualHash, ByteLength = bytes.Length, InputContract = inputContract,
            Content = bytes, CreatedAt = DateTimeOffset.UtcNow };
        db.RecipeModels.Add(model);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return Results.Ok((await db.RecipeModels.AsNoTracking().SingleAsync(x => x.Sha256 == actualHash, token)).View());
        }
        return Results.Created("/api/recipes/models", model.View());
    }
}
