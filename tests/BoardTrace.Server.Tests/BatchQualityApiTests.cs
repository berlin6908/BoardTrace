using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Inspections;

namespace BoardTrace.Server.Tests;

// Re-use the isolated SQL batch fixture, including real personnel/device cookies.
public sealed partial class BatchApiTests
{
    [Fact]
    public async Task QualityQueuePermissionsAndFinalDispositionPreserveMachineResults()
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server);
        var failed = Production(server, batch, session, 1, true);
        var passed = Production(server, batch, session, 2, false);
        await server.Upload(failed); await server.Upload(passed);
        var engineering = server.Record(batch, InspectionPurpose.EngineeringReplay) with { BatchId = null };
        await server.Upload(engineering);
        var queueUrl = $"/api/quality/queue?batchId={batch.Batch.Id}&stationId=STATION-A&page=1&pageSize=1";
        foreach (var human in new[] { server.Operator, server.Engineer, server.Quality })
        {
            var queue = (await human.GetFromJsonAsync<QualityQueuePage>(queueUrl))!;
            Assert.Equal(1, queue.Total); Assert.Equal(failed.Id, Assert.Single(queue.Items).InspectionId);
            Assert.Equal(batch.Batch.BatchNumber, queue.Items[0].BatchNumber);
            await server.Status(human.GetAsync($"/api/quality/inspections/{failed.Id}"), HttpStatusCode.OK);
            await server.Status(human.GetAsync($"/api/batches/{batch.Batch.Id}/rework-orders"), HttpStatusCode.OK);
        }
        foreach (var url in new[] { queueUrl, $"/api/quality/inspections/{failed.Id}", $"/api/batches/{batch.Batch.Id}/rework-orders" })
            await server.Status(server.Device.GetAsync(url), HttpStatusCode.Forbidden);
        await server.Status(server.Quality.GetAsync("/api/quality/queue?page=0"), HttpStatusCode.BadRequest);
        Assert.Empty((await server.Quality.GetFromJsonAsync<QualityQueuePage>("/api/quality/queue?stationId=OTHER"))!.Items);
        foreach (var unauthorized in new[] { server.Operator, server.Engineer, server.Device })
            await server.Status(unauthorized.PostAsJsonAsync(ReviewUrl(failed.Id), new CreateInspectionReviewRequest(ReviewDisposition.Accept, "review")), HttpStatusCode.Forbidden);
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(failed.Id), new CreateInspectionReviewRequest(ReviewDisposition.Accept, " ")), HttpStatusCode.BadRequest);
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(failed.Id), new CreateInspectionReviewRequest(ReviewDisposition.ResolveTechnicalIssue, "not a technical result")), HttpStatusCode.Conflict);
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(engineering.Id), new CreateInspectionReviewRequest(ReviewDisposition.Accept, "engineering")), HttpStatusCode.Conflict);
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(session.FirstArticleInspectionId), new CreateInspectionReviewRequest(ReviewDisposition.Accept, "first article")), HttpStatusCode.Conflict);
        var before = (await server.Quality.GetFromJsonAsync<InspectionDetail>($"/api/inspections/{failed.Id}"))!;
        var accepted = await Review(server, failed.Id, ReviewDisposition.Accept, "人工确认可接受，保留机器不合格");
        Assert.Equal("quality", accepted.Review!.ReviewedById);
        Assert.Null(accepted.ReworkOrder);
        await Review(server, passed.Id, ReviewDisposition.Reject, "人工拒收，保留机器合格");
        var after = (await server.Quality.GetFromJsonAsync<InspectionDetail>($"/api/inspections/{failed.Id}"))!;
        Assert.Equal(before.ContentHash, after.ContentHash);
        Assert.Equal(QualityDecision.Fail, after.Inspection.Decision);
        Assert.Equal(failed.Defects[0].Box, Assert.Single(after.Inspection.Defects).Box);
        Assert.Equal(failed.TestedImage, await server.Quality.GetByteArrayAsync($"/api/inspections/{failed.Id}/images/tested"));
        Assert.Equal(failed.ReferenceImage, await server.Quality.GetByteArrayAsync($"/api/inspections/{failed.Id}/images/reference"));
        Assert.Equal(QualityDecision.Pass, (await server.Quality.GetFromJsonAsync<InspectionDetail>($"/api/inspections/{passed.Id}"))!.Inspection.Decision);
        Assert.Empty((await server.Quality.GetFromJsonAsync<QualityQueuePage>(queueUrl))!.Items);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{failed.Id}", failed), HttpStatusCode.OK);
        await server.Status(server.Device.PostAsync("/api/auth/logout", null), HttpStatusCode.NoContent);
        await server.Status(server.Device.GetAsync(queueUrl), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task QualityReviewAndOrderRollbackTogetherAndConcurrentRetriesKeepOneOriginal()
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server);
        var record = Production(server, batch, session, 1, true);
        await server.Upload(record);
        var request = new CreateInspectionReviewRequest(ReviewDisposition.Rework, "修复后重新检测");
        await server.Execute("CREATE TRIGGER dbo.RejectRework ON dbo.ReworkOrders AFTER INSERT AS BEGIN THROW 51000, 'isolated rework failure', 1; END");
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(record.Id), request), HttpStatusCode.InternalServerError);
        var unreviewed = (await server.Quality.GetFromJsonAsync<InspectionQualityDetails>($"/api/quality/inspections/{record.Id}"))!;
        Assert.Null(unreviewed.Review); Assert.Null(unreviewed.ReworkOrder);
        Assert.Single((await server.Quality.GetFromJsonAsync<QualityQueuePage>("/api/quality/queue"))!.Items);
        await server.Execute("DROP TRIGGER dbo.RejectRework");
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => server.Quality.PostAsJsonAsync(ReviewUrl(record.Id), request)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var details = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<InspectionQualityDetails>()));
        foreach (var response in responses) response.Dispose();
        Assert.All(details, d => Assert.Equal(details[0], d));
        var originalOrder = Assert.Single((await server.Operator.GetFromJsonAsync<ReworkOrder[]>($"/api/batches/{batch.Batch.Id}/rework-orders"))!);
        Assert.Equal(record.Id, originalOrder.OriginalInspectionId);
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(record.Id), request with { Note = "不同意见" }), HttpStatusCode.Conflict);
        await server.Login(server.Quality, "quality-2");
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(record.Id), request), HttpStatusCode.Conflict);
        Assert.Equal(details[0], await server.Quality.GetFromJsonAsync<InspectionQualityDetails>($"/api/quality/inspections/{record.Id}"));
    }

    [Fact]
    public async Task QualityReinspectionConsumesTechnicalAttemptAndPreservesItsFullChain()
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server);
        var original = Production(server, batch, session, 1, true);
        await server.Upload(original);
        var first = (await Review(server, original.Id, ReviewDisposition.Rework, "修复原产品" )).ReworkOrder!;
        var interrupted = Reinspection(server, batch, session, first) with
        {
            ExecutionStatus = InspectionExecution.Interrupted, Decision = QualityDecision.NotEvaluated,
            Defects = [], TestedImage = null, ReferenceImage = null, Error = "断电恢复：本次已接件，不自动重采"
        };
        await server.Upload(interrupted);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{interrupted.Id}", interrupted), HttpStatusCode.OK);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{Guid.NewGuid()}", interrupted), HttpStatusCode.BadRequest);
        var duplicate = Reinspection(server, batch, session, first);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{duplicate.Id}", duplicate), HttpStatusCode.Conflict);
        Assert.Empty((await server.Operator.GetFromJsonAsync<ReworkOrder[]>($"/api/batches/{batch.Batch.Id}/rework-orders"))!);
        await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(interrupted.Id), new CreateInspectionReviewRequest(ReviewDisposition.Accept, "技术失败不能接受质量")), HttpStatusCode.Conflict);
        var second = (await Review(server, interrupted.Id, ReviewDisposition.Rework, "排除采集故障后复检")).ReworkOrder!;
        var completed = Reinspection(server, batch, session, second);
        await server.Upload(completed);
        var queued = Assert.Single((await server.Quality.GetFromJsonAsync<QualityQueuePage>("/api/quality/queue"))!.Items);
        Assert.Equal(completed.Id, queued.InspectionId); Assert.Equal(QualityDecision.Pass, queued.Decision);
        var originalDetail = (await server.Quality.GetFromJsonAsync<InspectionQualityDetails>($"/api/quality/inspections/{original.Id}"))!;
        Assert.Null(originalDetail.SourceReworkOrder); Assert.Equal(first, originalDetail.ReworkOrder); Assert.Equal(interrupted.Id, originalDetail.ReinspectionId);
        var interruptedDetail = (await server.Quality.GetFromJsonAsync<InspectionQualityDetails>($"/api/quality/inspections/{interrupted.Id}"))!;
        Assert.Equal(first, interruptedDetail.SourceReworkOrder); Assert.Equal(second, interruptedDetail.ReworkOrder); Assert.Equal(completed.Id, interruptedDetail.ReinspectionId);
        var final = await Review(server, completed.Id, ReviewDisposition.Accept, "复检结果接受");
        Assert.Equal(second, final.SourceReworkOrder); Assert.Null(final.ReworkOrder); Assert.Null(final.ReinspectionId);
        var batchDetail = (await server.Operator.GetFromJsonAsync<BatchDetails>($"/api/batches/{batch.Batch.Id}"))!;
        Assert.Equal(1, batchDetail.ReceivedProductionCount); Assert.Equal(0, batchDetail.TechnicalFailureCount);
        Assert.Empty((await server.Quality.GetFromJsonAsync<QualityQueuePage>("/api/quality/queue"))!.Items);
        var returned = (await server.Operator.GetFromJsonAsync<InspectionDetail>($"/api/inspections/{completed.Id}"))!;
        Assert.Null(returned.Inspection.ProductionSequence); Assert.Equal(second.Id, returned.Inspection.ReworkOrderId);
        Assert.Equal(InspectionTransfer.Hash(original), (await server.Operator.GetFromJsonAsync<InspectionDetail>($"/api/inspections/{original.Id}"))!.ContentHash);
    }

    [Fact]
    public async Task QualityReinspectionChecksImmutableOrderAndConcurrentConsumptionHasOneWinner()
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server);
        var original = Production(server, batch, session, 1, true);
        await server.Upload(original);
        var order = (await Review(server, original.Id, ReviewDisposition.Rework, "固定来源复检")).ReworkOrder!;
        var valid = Reinspection(server, batch, session, order);
        foreach (var invalid in new[]
        {
            valid with { ProductId = "另一产品" }, valid with { SampleId = "another-sample" },
            valid with { BatchId = Guid.NewGuid() }, valid with { RecipeId = Guid.NewGuid().ToString() },
            valid with { ExecutionSessionId = Guid.NewGuid() }, valid with { OperatorId = "operator-2", OperatorName = "operator-2" },
            valid with { StartedAt = session.ExpiresAt }, valid with { StartedAt = order.CreatedAt.AddSeconds(-1) },
            valid with { ProductionSequence = 2 }, valid with { ReworkOrderId = Guid.NewGuid() }
        }) await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{invalid.Id}", invalid), HttpStatusCode.Conflict);
        await server.Status(server.OtherDevice.PutAsJsonAsync($"/api/inspections/{valid.Id}", valid), HttpStatusCode.Forbidden);
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{valid.Id}", valid with { ReworkOrderId = null }), HttpStatusCode.BadRequest);
        var illegalPurpose = Production(server, batch, session, 2, false) with { ReworkOrderId = order.Id };
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{illegalPurpose.Id}", illegalPurpose), HttpStatusCode.BadRequest);
        var competing = valid with { Id = Guid.NewGuid(), ExecutionStatus = InspectionExecution.Failed, Decision = QualityDecision.NotEvaluated, TestedImage = null, ReferenceImage = null, Error = "采集失败" };
        var responses = await Task.WhenAll(new[] { valid, competing }.Select(record => server.Device.PutAsJsonAsync($"/api/inspections/{record.Id}", record)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in responses) response.Dispose();
        Assert.Empty((await server.Operator.GetFromJsonAsync<ReworkOrder[]>($"/api/batches/{batch.Batch.Id}/rework-orders"))!);
        var winner = (await server.Operator.GetFromJsonAsync<InspectionQualityDetails>($"/api/quality/inspections/{original.Id}"))!.ReinspectionId!.Value;
        if (winner == competing.Id) await Review(server, winner, ReviewDisposition.ResolveTechnicalIssue, "技术问题已处理，无质量判定");
        else await Review(server, winner, ReviewDisposition.Reject, "人工拒收但保留机器Pass");
        Assert.Equal(1, (await server.Operator.GetFromJsonAsync<BatchDetails>($"/api/batches/{batch.Batch.Id}"))!.ReceivedProductionCount);
    }

    [Theory]
    [InlineData(InspectionExecution.Failed)]
    [InlineData(InspectionExecution.Interrupted)]
    public async Task QualityTechnicalResolutionPreservesNotEvaluated(InspectionExecution status)
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server);
        var technical = Production(server, batch, session, 1, false) with
        {
            ExecutionStatus = status, Decision = QualityDecision.NotEvaluated,
            TestedImage = null, ReferenceImage = null, Error = "isolated technical attempt"
        };
        await server.Upload(technical);
        Assert.Equal(technical.Id, Assert.Single((await server.Quality.GetFromJsonAsync<QualityQueuePage>("/api/quality/queue"))!.Items).InspectionId);
        foreach (var disposition in new[] { ReviewDisposition.Accept, ReviewDisposition.Reject })
            await server.Status(server.Quality.PostAsJsonAsync(ReviewUrl(technical.Id), new CreateInspectionReviewRequest(disposition, "cannot decide quality")), HttpStatusCode.Conflict);
        var resolved = await Review(server, technical.Id, ReviewDisposition.ResolveTechnicalIssue, "技术异常已处理，原件不作质量判定");
        Assert.Null(resolved.ReworkOrder);
        Assert.Empty((await server.Quality.GetFromJsonAsync<QualityQueuePage>("/api/quality/queue"))!.Items);
        var original = (await server.Quality.GetFromJsonAsync<InspectionDetail>($"/api/inspections/{technical.Id}"))!;
        Assert.Equal(status, original.Inspection.ExecutionStatus); Assert.Equal(QualityDecision.NotEvaluated, original.Inspection.Decision);
        Assert.Equal(InspectionTransfer.Hash(technical), original.ContentHash);
    }

    [Fact]
    public async Task QualityReinspectionAfterFullProductionUsesHistoricalSessionAtAcceptanceTime()
    {
        await using var server = await BatchServer.CreateAsync();
        var (batch, session) = await StartQualityBatch(server);
        var original = Production(server, batch, session, 1, true);
        await server.Upload(original);
        await server.Upload(Production(server, batch, session, 2, false));
        var order = (await Review(server, original.Id, ReviewDisposition.Rework, "计划已满后的原产品复检")).ReworkOrder!;
        var reinspection = Reinspection(server, batch, session, order);
        var expiry = reinspection.StartedAt.AddMilliseconds(10);
        await server.Execute($"UPDATE BatchExecutionSessions SET ExpiresAt='{expiry:O}' WHERE Id='{session.Id}'");
        await Task.Delay(30);
        Assert.True(DateTimeOffset.UtcNow >= expiry);
        var expiredAcceptance = reinspection with { StartedAt = expiry };
        await server.Status(server.Device.PutAsJsonAsync($"/api/inspections/{expiredAcceptance.Id}", expiredAcceptance), HttpStatusCode.Conflict);
        await server.Upload(reinspection);
        var details = (await server.Operator.GetFromJsonAsync<BatchDetails>($"/api/batches/{batch.Batch.Id}"))!;
        Assert.Equal(2, details.ReceivedProductionCount);
        Assert.Equal(batch.Batch.PlannedQuantity, details.ReceivedProductionCount);
        Assert.Equal(reinspection.Id, (await server.Operator.GetFromJsonAsync<InspectionQualityDetails>($"/api/quality/inspections/{original.Id}"))!.ReinspectionId);
    }

    private static string ReviewUrl(Guid id) => $"/api/quality/inspections/{id}/review";

    private static async Task<(BatchDetails, BatchExecutionSession)> StartQualityBatch(BatchServer server)
    {
        var batch = await server.CreateBatch();
        var first = server.Record(batch, InspectionPurpose.FirstArticle);
        await server.Upload(first);
        await server.Status(server.Quality.PutAsJsonAsync($"/api/batches/{batch.Batch.Id}/first-article-approval", new ApproveFirstArticleRequest(first.Id)), HttpStatusCode.Created);
        using var response = await server.Operator.PostAsJsonAsync($"/api/batches/{batch.Batch.Id}/execution-sessions", new StartBatchRequest("STATION-A", server.Version.BundleHash, server.ArchiveId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (batch, (await response.Content.ReadFromJsonAsync<BatchExecutionSession>())!);
    }

    private static InspectionRecord Production(BatchServer server, BatchDetails batch, BatchExecutionSession session, int sequence, bool fail) =>
        server.Record(batch, InspectionPurpose.Production) with { ExecutionSessionId = session.Id, ProductionSequence = sequence,
            Decision = fail ? QualityDecision.Fail : QualityDecision.Pass, Defects = fail ? [new DefectBox([1, 1, 4, 4], 1, .9, 9)] : [] };

    private static InspectionRecord Reinspection(BatchServer server, BatchDetails batch, BatchExecutionSession session, ReworkOrder order) =>
        server.Record(batch, InspectionPurpose.Reinspection) with { ExecutionSessionId = session.Id, ReworkOrderId = order.Id, ProductId = order.ProductId, SampleId = order.SampleId };

    private static async Task<InspectionQualityDetails> Review(BatchServer server, Guid id, ReviewDisposition disposition, string note)
    {
        using var response = await server.Quality.PostAsJsonAsync(ReviewUrl(id), new CreateInspectionReviewRequest(disposition, note));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<InspectionQualityDetails>())!;
    }
}
