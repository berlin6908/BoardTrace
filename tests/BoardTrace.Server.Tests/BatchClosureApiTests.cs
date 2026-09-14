using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;

namespace BoardTrace.Server.Tests;

public sealed partial class BatchApiTests
{
    [Fact]
    public async Task RuntimeAuthenticationLatestSnapshotAndServerClockFreshness()
    {
        await using var server = await BatchServer.CreateAsync();
        var unseen = (await server.Operator.GetFromJsonAsync<StationRuntimeView[]>("/api/stations/runtime"))!;
        Assert.Equal(2, unseen.Length); Assert.All(unseen, row => { Assert.Null(row.Runtime); Assert.Null(row.ReceivedAt); Assert.False(row.IsOnline); });
        var input = new StationRuntimeUpdate(null, null, 0, 0, 0, 2, false, true, "AwaitingAck", "待恢复确认", DateTimeOffset.Parse("2000-01-01T00:00:00Z"));
        await server.Status(server.Operator.PutAsJsonAsync("/api/stations/STATION-A/runtime", input), HttpStatusCode.Forbidden);
        await server.Status(server.OtherDevice.PutAsJsonAsync("/api/stations/STATION-A/runtime", input), HttpStatusCode.Forbidden);
        await server.Status(server.Device.PutAsJsonAsync("/api/stations/STATION-A/runtime", input with { ProductionCount = -1 }), HttpStatusCode.BadRequest);
        var receipt = await ReportRuntime(server, input);
        Assert.True(receipt.ReceivedAt > input.ObservedAt);
        var current = (await server.Quality.GetFromJsonAsync<StationRuntimeView[]>("/api/stations/runtime"))!.Single(row => row.StationId == "STATION-A");
        Assert.Equal(input, current.Runtime); Assert.True(current.IsOnline);
        await server.Status(server.Device.GetAsync("/api/stations/runtime"), HttpStatusCode.Forbidden);
        await server.Execute("UPDATE StationRuntime SET ReceivedAt=DATEADD(second,-31,SYSDATETIMEOFFSET())");
        Assert.False((await server.Quality.GetFromJsonAsync<StationRuntimeView[]>("/api/stations/runtime"))!.Single(row => row.StationId == "STATION-A").IsOnline);
        await ReportRuntime(server, input with { PendingUploads = 0, HasUnacknowledgedPlc = false, State = "Idle", Alarm = null });
        var latest = (await server.Operator.GetFromJsonAsync<StationRuntimeView[]>("/api/stations/runtime"))!.Single(row => row.StationId == "STATION-A");
        Assert.True(latest.IsOnline); Assert.Equal(0, latest.Runtime!.PendingUploads); Assert.Null(latest.Runtime.Alarm);
    }

    [Fact]
    public async Task ClosureRequiresAllRuntimeAndQualityFactsAndCommitsAuditAtomically()
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server);
        var id = batch.Batch.Id;
        var initial = await Closure(server, id);
        Assert.False(initial.CanClose); Assert.Contains(initial.Blockers, value => value.Contains("现场上报"));
        Assert.Contains(initial.Blockers, value => value.Contains("计划生产"));
        var failed = Production(server, batch, session, 1, true);
        await server.Upload(failed);
        Assert.Equal(1, (await Closure(server, id)).UnreviewedCount);
        var order = (await Review(server, failed.Id, ReviewDisposition.Rework, "关闭前先返工")).ReworkOrder!;
        Assert.Equal(1, (await Closure(server, id)).PendingReworkOrders);
        var reinspection = Reinspection(server, batch, session, order);
        await server.Upload(reinspection);
        var second = Production(server, batch, session, 2, false);
        await server.Upload(second);
        Assert.Equal(1, (await Closure(server, id)).UnreviewedCount);
        await Review(server, reinspection.Id, ReviewDisposition.Accept, "复检已复核");
        var good = Runtime(batch, server.ArchiveId, 1, 2, 1);
        foreach (var (invalid, reason) in new (StationRuntimeUpdate, string)[]
        {
            (good with { PendingUploads = 1 }, "待上传"), (good with { HasStartedInspection = true }, "尚未结束"),
            (good with { HasUnacknowledgedPlc = true }, "未ACK"), (good with { FirstArticleCount = 2 }, "首件现场"),
            (good with { ProductionCount = 1 }, "生产现场"), (good with { ReinspectionCount = 0 }, "复检现场"),
            (good with { ArchiveId = Guid.NewGuid() }, "执行档案"), (good with { BatchId = null, ArchiveId = null }, "当前批次")
        })
        {
            await ReportRuntime(server, invalid);
            using var blocked = await server.Quality.PostAsync($"/api/batches/{id}/close", null);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            var check = (await blocked.Content.ReadFromJsonAsync<BatchClosureCheck>())!;
            Assert.Contains(check.Blockers, value => value.Contains(reason)); Assert.Null(check.Closure);
        }
        await ReportRuntime(server, good with { ObservedAt = DateTimeOffset.UtcNow.AddYears(1) });
        await server.Execute("UPDATE StationRuntime SET ReceivedAt=DATEADD(second,-31,SYSDATETIMEOFFSET())");
        Assert.Contains((await Closure(server, id)).Blockers, value => value.Contains("超过30秒"));
        await ReportRuntime(server, good);
        Assert.True((await Closure(server, id)).CanClose);
        foreach (var unauthorized in new[] { server.Operator, server.Engineer, server.Device })
            await server.Status(unauthorized.PostAsync($"/api/batches/{id}/close", null), HttpStatusCode.Forbidden);
        await server.Execute("CREATE TRIGGER dbo.RejectClosure ON dbo.BatchClosures AFTER INSERT AS BEGIN THROW 51000, 'isolated close failure', 1; END");
        await server.Status(server.Quality.PostAsync($"/api/batches/{id}/close", null), HttpStatusCode.InternalServerError);
        var rolledBack = await Closure(server, id);
        Assert.Equal(BatchStatus.InProgress, rolledBack.Status); Assert.Null(rolledBack.Closure); Assert.True(rolledBack.CanClose);
        await server.Execute("DROP TRIGGER dbo.RejectClosure");
        var closes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => server.Quality.PostAsync($"/api/batches/{id}/close", null)));
        Assert.Single(closes, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(2, closes.Count(response => response.StatusCode == HttpStatusCode.OK));
        var audits = await Task.WhenAll(closes.Select(response => response.Content.ReadFromJsonAsync<BatchClosureAudit>()));
        foreach (var response in closes) response.Dispose();
        Assert.All(audits, audit => Assert.Equal(audits[0], audit)); Assert.Equal("quality", audits[0]!.ClosedById);
        var before = await server.Operator.GetStringAsync($"/api/batches/{id}/report");
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(second.Id), new CreateInspectionReviewRequest(ReviewDisposition.Rework, "关闭后新返工")), HttpStatusCode.Conflict);
        var late = second with { Id = Guid.NewGuid() };
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{late.Id}", late), HttpStatusCode.Conflict);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{second.Id}", second), HttpStatusCode.OK);
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(failed.Id), new CreateInspectionReviewRequest(ReviewDisposition.Rework, "关闭前先返工")), HttpStatusCode.OK);
        Assert.Equal(before, await server.Operator.GetStringAsync($"/api/batches/{id}/report"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosureSerializesWithNewReviewOrNewReceipt(bool newReview)
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server);
        var first = Production(server, batch, session, 1, false);
        await server.Upload(first); await server.Upload(Production(server, batch, session, 2, false));
        await ReportRuntime(server, Runtime(batch, server.ArchiveId, 1, 2, 0));
        var extraFirstArticle = server.Record(batch, InspectionPurpose.FirstArticle) with { StartedAt = batch.Batch.CreatedAt, CompletedAt = batch.Batch.CreatedAt.AddMilliseconds(1) };
        var mutation = newReview
            ? server.Quality.PostAsJsonAsync(ReviewUrl(first.Id), new CreateInspectionReviewRequest(ReviewDisposition.Rework, "与关闭并发的新返工"))
            : server.Device.PutAsJsonAsync($"/api/inspections/{extraFirstArticle.Id}", extraFirstArticle);
        var responses = await Task.WhenAll(server.Quality.PostAsync($"/api/batches/{batch.Batch.Id}/close", null), mutation);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var check = await Closure(server, batch.Batch.Id);
        if (responses[0].StatusCode == HttpStatusCode.Created)
        {
            Assert.Equal(BatchStatus.Closed, check.Status); Assert.Equal(0, check.PendingReworkOrders);
            Assert.Equal(1, check.CentralCounts.FirstArticle);
        }
        else
        {
            Assert.Equal(BatchStatus.InProgress, check.Status); Assert.Null(check.Closure);
            if (newReview) Assert.Equal(1, check.PendingReworkOrders);
            else Assert.Equal(2, check.CentralCounts.FirstArticle);
        }
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task BatchReportSeparatesFirstValidProductionTechnicalReinspectionAndConstructedInputs()
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server, 6);
        InspectionRecord Make(int sequence, string product, bool fail = false) => Production(server, batch, session, sequence, fail) with { ProductId = product, SourceKind = "Replay" };
        var technical = Make(1, "P1") with { ExecutionStatus = InspectionExecution.Failed, Decision = QualityDecision.NotEvaluated, TestedImage = null, ReferenceImage = null, Error = "采集失败" };
        var valid = Make(2, "P1");
        var later = Make(3, "P1", true);
        var failed = Make(4, "P2", true);
        var constructed = Make(5, "P3") with { SourceKind = "ConstructedNormal" };
        var onlyTechnical = Make(6, "P4") with { ExecutionStatus = InspectionExecution.Interrupted, Decision = QualityDecision.NotEvaluated, TestedImage = null, ReferenceImage = null, Error = "断电" };
        foreach (var record in new[] { technical, valid, later, failed, constructed, onlyTechnical }) await server.Upload(record);
        await Review(server, technical.Id, ReviewDisposition.ResolveTechnicalIssue, "技术处理");
        await Review(server, later.Id, ReviewDisposition.Accept, "人工接受不改变首次有效结果");
        var note = "返工意见含逗号,引号\"与\n换行";
        var order = (await Review(server, failed.Id, ReviewDisposition.Rework, note)).ReworkOrder!;
        var reinspection = Reinspection(server, batch, session, order);
        await server.Upload(reinspection); await Review(server, reinspection.Id, ReviewDisposition.Reject, "机器Pass仍可人工拒收");
        using var json = await server.Operator.GetAsync($"/api/batches/{batch.Batch.Id}/report");
        Assert.Equal($"batch-{batch.Batch.Id:D}.json", json.Content.Headers.ContentDisposition!.FileNameStar);
        var report = (await json.Content.ReadFromJsonAsync<BatchOperationalReport>())!;
        var production = report.Summary.Purposes.Single(row => row.Purpose == InspectionPurpose.Production);
        Assert.Equal(new BatchPurposeStatistics(InspectionPurpose.Production, 6, 4, 2, 2, 2), production);
        Assert.Equal(new FirstInspectionYield(3, 2, 1, 2d / 3), report.Summary.FirstInspection);
        Assert.Equal(new FirstInspectionYield(2, 1, 1, .5), report.Summary.BySource.Single(row => row.SourceKind == "Replay").FirstInspection);
        Assert.Equal(new FirstInspectionYield(1, 1, 0, 1), report.Summary.BySource.Single(row => row.SourceKind == "ConstructedNormal").FirstInspection);
        Assert.Equal(1, report.Summary.Purposes.Single(row => row.Purpose == InspectionPurpose.Reinspection).Attempts);
        Assert.All(report.Summary.Reviews, disposition => Assert.Equal(1, disposition.Count));
        Assert.Equal(8, report.Inspections.Count);
        var source = report.Inspections.Single(row => row.Inspection.Id == failed.Id);
        Assert.Equal(InspectionTransfer.Hash(failed), source.ContentHash); Assert.Equal(order.Id, source.Quality.ReworkOrder!.Id);
        Assert.Equal(reinspection.Id, source.Quality.ReinspectionId); Assert.Equal(QualityDecision.Fail, source.Inspection.Decision);
        Assert.Null(source.Inspection.TestedImage);
        Assert.Equal(failed.TestedImage, await server.Operator.GetByteArrayAsync(source.TestedImageUrl));
        Assert.Contains("模拟", report.Scope);
        using var csv = await server.Quality.GetAsync($"/api/batches/{batch.Batch.Id}/report.csv");
        Assert.Equal($"batch-{batch.Batch.Id:D}.csv", csv.Content.Headers.ContentDisposition!.FileNameStar);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("FirstEvaluatedProducts", text); Assert.Contains("ConstructedNormalFirstProducts", text);
        Assert.Contains(note.Replace("\"", "\"\""), text); Assert.Contains(failed.Id.ToString(), text); Assert.Contains(reinspection.Id.ToString(), text);
        await server.Status(server.Device.GetAsync($"/api/batches/{batch.Batch.Id}/report"), HttpStatusCode.Forbidden);
    }

    private static StationRuntimeUpdate Runtime(BatchDetails batch, Guid archive, int first, int production, int reins) =>
        new(batch.Batch.Id, archive, first, production, reins, 0, false, false, "Idle", null, DateTimeOffset.UtcNow);

    private static async Task<StationRuntimeReceipt> ReportRuntime(BatchServer server, StationRuntimeUpdate runtime)
    {
        using var response = await server.Device.PutAsJsonAsync("/api/stations/STATION-A/runtime", runtime);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<StationRuntimeReceipt>())!;
    }

    private static async Task<BatchClosureCheck> Closure(BatchServer server, Guid id) =>
        (await server.Quality.GetFromJsonAsync<BatchClosureCheck>($"/api/batches/{id}/closure"))!;
}
