using System.Security.Claims;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Stations;

public sealed class StationRuntimeState
{
    public required string StationId { get; set; }
    public required string Document { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    public StationRuntimeUpdate Read() => JsonSerializer.Deserialize<StationRuntimeUpdate>(Document)!;
}

public static class StationRuntimeEndpoints
{
    public static void MapStationRuntime(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/stations/{stationId}/runtime", Receive).RequireAuthorization("Station");
        app.MapGet("/api/stations/runtime", List).RequireAuthorization("Human");
    }

    private static async Task<IResult> Receive(string stationId, StationRuntimeUpdate input, ClaimsPrincipal principal,
        UserManager<BoardTraceUser> users, BoardTraceDbContext db, CancellationToken token)
    {
        if ((await users.GetUserAsync(principal))?.StationId != stationId) return Results.Forbid();
        if ((input.BatchId is null) != (input.ArchiveId is null) || input.BatchId == Guid.Empty || input.ArchiveId == Guid.Empty ||
            input.FirstArticleCount < 0 || input.ProductionCount < 0 || input.ReinspectionCount < 0 || input.PendingUploads < 0 ||
            string.IsNullOrWhiteSpace(input.State) || input.State.Length > 64 || input.Alarm?.Length > 2000)
            return Results.BadRequest("现场状态、批次档案身份或计数无效。");
        if (input.BatchId is not null && !await db.Batches.AnyAsync(batch => batch.Id == input.BatchId && batch.StationId == stationId, token))
            return Results.Conflict("上报批次不属于该工位。");
        var document = JsonSerializer.Serialize(input);
        var receivedAt = DateTimeOffset.UtcNow;
        if (await Update(db, stationId, document, receivedAt, token) == 0)
        {
            db.StationRuntime.Add(new StationRuntimeState { StationId = stationId, Document = document, ReceivedAt = receivedAt });
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
            {
                db.ChangeTracker.Clear();
                receivedAt = DateTimeOffset.UtcNow;
                await Update(db, stationId, document, receivedAt, token);
            }
        }
        return Results.Ok(new StationRuntimeReceipt(stationId, receivedAt));
    }

    private static Task<int> Update(BoardTraceDbContext db, string id, string document, DateTimeOffset now, CancellationToken token) =>
        db.StationRuntime.Where(row => row.StationId == id).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Document, document).SetProperty(row => row.ReceivedAt, now), token);

    private static async Task<IResult> List(UserManager<BoardTraceUser> users, BoardTraceDbContext db, CancellationToken token)
    {
        var stations = (await users.GetUsersInRoleAsync("Station")).Where(user => user.StationId is not null)
            .OrderBy(user => user.StationId).ToArray();
        var states = await db.StationRuntime.AsNoTracking().ToDictionaryAsync(row => row.StationId, token);
        var batches = await db.Batches.AsNoTracking().Select(batch => new { batch.Id, batch.BatchNumber }).ToDictionaryAsync(batch => batch.Id, token);
        var now = DateTimeOffset.UtcNow;
        return Results.Ok(stations.Select(station =>
        {
            var saved = states.GetValueOrDefault(station.StationId!);
            var runtime = saved?.Read();
            var batchNumber = runtime?.BatchId is Guid id ? batches.GetValueOrDefault(id)?.BatchNumber : null;
            return new StationRuntimeView(station.StationId!, station.DisplayName, runtime, saved?.ReceivedAt,
                saved is not null && IsFresh(saved.ReceivedAt, now), batchNumber);
        }).ToArray());
    }

    internal static bool IsFresh(DateTimeOffset receivedAt, DateTimeOffset now) => receivedAt >= now.AddSeconds(-30);
}
