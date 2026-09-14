using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Controls;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;

namespace BoardTrace.Station.Smoke;

public static partial class Program
{
    private sealed record ReworkUiContext(StationOptions Options, string AccountsPath, Guid RecipeVersionId, string Output);
    private sealed record ReworkLiveResult(Guid BatchId, Guid OriginalInspectionId, Guid ReworkOrderId,
        Guid ReinspectionId, string OriginalHash, string ReinspectionHash, string StationDatabase, string WpfEvidence);

    private static async Task VerifyReworkLiveAsync(string scope, string contextPath, string output)
    {
        var context = JsonSerializer.Deserialize<ReworkUiContext>(await File.ReadAllTextAsync(contextPath), LiveJson)
            ?? throw new InvalidDataException("Missing rework SQL context.");
        Directory.CreateDirectory(context.Output);
        var accounts = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(context.AccountsPath), LiveJson)!;
        using var quality = StationAuthentication.CreateClient(context.Options.ServerUrl);
        using (var login = await quality.PostAsJsonAsync("api/auth/login", new LoginRequest("quality", accounts["quality"])))
            login.EnsureSuccessStatusCode();
        if (scope == "rework-live-verify")
        {
            await VerifyFinalReworkReview(context, quality);
            return;
        }
        using var process = StationAuthentication.CreateClient(context.Options.ServerUrl);
        using (var login = await process.PostAsJsonAsync("api/auth/login", new LoginRequest("process", accounts["process"])))
            login.EnsureSuccessStatusCode();
        using var created = await process.PostAsJsonAsync("api/batches", new CreateBatchRequest(
            "SIM-REWORK-" + Guid.NewGuid().ToString("N")[..8], "隔离模拟 PCB", "TOP-640x640", 1,
            context.Options.StationId, context.RecipeVersionId));
        Require(created.StatusCode == HttpStatusCode.Created,
            "Live rework batch creation: " + await created.Content.ReadAsStringAsync());
        var batch = (await created.Content.ReadFromJsonAsync<BatchDetails>())!;
        var loginWindow = await OpenLiveLogin(context.Options, context.AccountsPath);
        await using var model = new StationViewModel(context.Options, loginWindow.AuthenticatedUser,
            loginWindow.PersonnelSession, false);
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await model.InitializeAsync();
            ((Expander)window.FindName("RecipeExpander")).IsExpanded = false;
            ((Expander)window.FindName("BatchExpander")).IsExpanded = true;
            await model.RefreshBatchesCommand.ExecuteAsync(null);
            model.SelectedBatch = model.AssignedBatches.Single(row => row.Summary.Batch.Id == batch.Batch.Id);
            await model.DownloadBatchCommand.ExecuteAsync(null);
            Require(model.RunCommand.CanExecute(null), "First article not ready: " + model.BatchNotice);
            var store = new LocalInspectionStore(context.Options.DatabasePath);
            model.ConstructedNormal = true;
            model.ProductId = "SIM-REWORK-FIRST-CONTROLLED-NORMAL";
            await model.RunCommand.ExecuteAsync(null);
            var first = store.Get(Guid.Parse(model.InspectionId))!;
            Require(first is { Purpose: InspectionPurpose.FirstArticle, Decision: QualityDecision.Pass,
                SourceKind: "ConstructedNormal" }, "Controlled-normal first article was not actually detected.");
            await AwaitBatchUiAsync(() => store.PendingCount() == 0, "live rework first article upload");
            using (var approval = await quality.PutAsJsonAsync($"api/batches/{batch.Batch.Id}/first-article-approval",
                       new ApproveFirstArticleRequest(first.Id)))
                Require(approval.StatusCode == HttpStatusCode.Created,
                    "First article approval: " + await approval.Content.ReadAsStringAsync());
            await model.RefreshActiveBatchCommand.ExecuteAsync(null);
            await model.StartBatchCommand.ExecuteAsync(null);
            Require(model.RunCommand.CanExecute(null), "Live rework batch cannot start: " + model.BatchNotice);
            model.ConstructedNormal = false;
            model.ProductId = "SIM-REWORK-PRODUCT-1";
            await model.RunCommand.ExecuteAsync(null);
            var original = store.Get(Guid.Parse(model.InspectionId))!;
            Require(original is { Purpose: InspectionPurpose.Production, ProductionSequence: 1,
                ExecutionStatus: InspectionExecution.Completed, Decision: QualityDecision.Fail }
                && original.Defects.Count > 0, "Original production did not detect actual validation-image defects.");
            await AwaitBatchUiAsync(() => store.PendingCount() == 0, "live rework original upload");
            Require(!model.RunCommand.CanExecute(null), "Full production quantity admitted another production attempt.");
            await VerifyLiveRecord(quality, store, original);
            await SnapshotAsync(window, Path.Combine(output, "01-full-quantity-original-fail.png"));

            var note = "隔离流程验证：原图存在实际算法检出的缺陷，要求返工后重新检测；原机器判定保留。";
            using var reviewResponse = await quality.PostAsJsonAsync($"api/quality/inspections/{original.Id}/review",
                new CreateInspectionReviewRequest(ReviewDisposition.Rework, note));
            Require(reviewResponse.StatusCode == HttpStatusCode.Created,
                "Live rework review: " + await reviewResponse.Content.ReadAsStringAsync());
            var originalQuality = (await reviewResponse.Content.ReadFromJsonAsync<InspectionQualityDetails>())!;
            var order = originalQuality.ReworkOrder ?? throw new InvalidDataException("Review did not atomically create a rework order.");
            Require(order.OriginalInspectionId == original.Id && order.ProductId == original.ProductId
                && originalQuality.Review?.Disposition == ReviewDisposition.Rework,
                "Rework order does not identify the original reviewed product.");
            await model.RefreshReworkOrdersCommand.ExecuteAsync(null);
            Require(model.ReworkOrders.Count == 1, "Real pending rework order was not offered by the station.");
            model.SelectedReworkOrder = model.ReworkOrders.Single();
            await model.EnterReinspectionCommand.ExecuteAsync(null);
            Require(model.IsReinspectionMode && !model.CanEditInspectionInput
                && model.ProductId == original.ProductId && model.RunCommand.CanExecute(null),
                "Cached rework order did not enable a fixed-input reinspection at full quantity: " + model.ReinspectionNotice);
            ((Expander)window.FindName("BatchExpander")).IsExpanded = false;
            ((Expander)window.FindName("ReinspectionExpander")).IsExpanded = true;
            await SnapshotAsync(window, Path.Combine(output, "02-reinspection-selected.png"));
            await model.RunCommand.ExecuteAsync(null);
            var repeated = store.Get(Guid.Parse(model.InspectionId))!;
            Require(repeated.Id != original.Id && repeated is { Purpose: InspectionPurpose.Reinspection,
                ProductionSequence: null, ExecutionStatus: InspectionExecution.Completed, Decision: QualityDecision.Fail }
                && repeated.ReworkOrderId == order.Id && repeated.BatchId == original.BatchId
                && repeated.ProductId == original.ProductId && repeated.SampleId == original.SampleId
                && repeated.RecipeId == original.RecipeId && repeated.ExecutionSessionId == original.ExecutionSessionId,
                "Actual reinspection lost the source order or changed the fixed product, sample, batch or recipe.");
            Require(repeated.TestedImage!.SequenceEqual(original.TestedImage!)
                && repeated.ReferenceImage!.SequenceEqual(original.ReferenceImage!) && repeated.Defects.Count > 0,
                "Same-image controlled rerun did not retain real evidence and defects.");
            Require(model.IsReinspectionMode && !model.RunCommand.CanExecute(null)
                && store.ReadActiveBatch()!.AcceptedProductionCount == 1,
                "Consumed order silently became production-ready or increased original production count.");
            await model.RunCommand.ExecuteAsync(null);
            Require(store.ReadRecent().Count == 3, "A consumed rework order created a second attempt.");
            model.SelectedHistory = model.History.Single(row => row.Id == repeated.Id);
            await model.ViewHistoryCommand.ExecuteAsync(null);
            await AwaitBatchUiAsync(() => store.PendingCount() == 0, "live reinspection upload");
            await VerifyLiveRecord(quality, store, original);
            await VerifyLiveRecord(quality, store, repeated);
            var refreshed = (await quality.GetFromJsonAsync<InspectionQualityDetails>($"api/quality/inspections/{original.Id}"))!;
            var child = (await quality.GetFromJsonAsync<InspectionQualityDetails>($"api/quality/inspections/{repeated.Id}"))!;
            var centralBatch = (await quality.GetFromJsonAsync<BatchDetails>($"api/batches/{batch.Batch.Id}"))!;
            var queue = (await quality.GetFromJsonAsync<QualityQueuePage>($"api/quality/queue?batchId={batch.Batch.Id}"))!;
            var pendingOrders = (await quality.GetFromJsonAsync<ReworkOrder[]>($"api/batches/{batch.Batch.Id}/rework-orders"))!;
            Require(refreshed.ReinspectionId == repeated.Id && child.SourceReworkOrder?.Id == order.Id
                && child.SourceReworkOrder.OriginalInspectionId == original.Id && child.Review is null
                && queue.Total == 1 && queue.Items.Single().InspectionId == repeated.Id
                && pendingOrders.Length == 0 && centralBatch.ReceivedProductionCount == 1,
                "SQL review queue, forward/backward links or production/reinspection counts differ.");
            await SnapshotAsync(window, Path.Combine(output, "03-reinspection-uploaded-awaiting-quality.png"));
            await WriteLive(Path.Combine(context.Output, "ready-for-browser.json"), new ReworkLiveResult(
                batch.Batch.Id, original.Id, order.Id, repeated.Id, InspectionTransfer.Hash(original),
                InspectionTransfer.Hash(repeated), context.Options.DatabasePath, output));
            await WriteLive(Path.Combine(context.Output, "before-final-review.json"), new { batch = centralBatch, queue, refreshed, child,
                scope = "真实WPF/SQL质量链；同一缺陷图再次检测仍Fail，非修复后的真实产品；方案版本由运行上下文指定，发布来源见本次运行证据。" });
            if (scope == "closure-live")
                await VerifyClosureLive(context, output, model, window, store, process, quality, batch, original, repeated);
        }
        finally { window.Close(); }
    }

    private static async Task VerifyClosureLive(ReworkUiContext context, string output, StationViewModel model,
        MainWindow window, LocalInspectionStore store, HttpClient process, HttpClient quality, BatchDetails batch,
        InspectionRecord original, InspectionRecord repeated)
    {
        var archive = store.ReadActiveBatch()!.ArchiveId;
        BatchClosureCheck? check = null;
        var reportDeadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while (DateTimeOffset.UtcNow < reportDeadline)
        {
            check = await quality.GetFromJsonAsync<BatchClosureCheck>($"api/batches/{batch.Batch.Id}/closure");
            if (check?.Station is { IsOnline: true, Runtime: { PendingUploads: 0, FirstArticleCount: 1,
                    ProductionCount: 1, ReinspectionCount: 1, HasStartedInspection: false, HasUnacknowledgedPlc: false } runtime }
                && runtime.ArchiveId == archive) break;
            await Task.Delay(500);
        }
        Require(check is { CanClose: false, UnreviewedCount: 1, PendingReworkOrders: 0, Station.IsOnline: true }
            && check.CentralCounts == new BatchCounts(1, 1, 1), "Actual WPF runtime did not identify the unreviewed batch closure blocker.");
        using (var blocked = await quality.PostAsync($"api/batches/{batch.Batch.Id}/close", null))
        {
            Require(blocked.StatusCode == HttpStatusCode.Conflict, "Unreviewed reinspection incorrectly allowed batch closure.");
            var response = await blocked.Content.ReadFromJsonAsync<BatchClosureCheck>();
            Require(response is { CanClose: false, UnreviewedCount: 1 }, "Blocked closure did not return its current quality blocker.");
        }
        await SnapshotAsync(window, Path.Combine(output, "04-live-runtime-awaiting-browser-close.png"));
        await WriteLive(Path.Combine(context.Output, "ready-for-close-browser.json"), new { batchId = batch.Batch.Id,
            archiveId = archive, check, runtimeReceivedAt = model.RuntimeReceivedAt, deadlineMinutes = 15 });
        Console.WriteLine("Actual WPF runtime is reporting. Waiting for browser quality disposition and batch close.");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            check = await quality.GetFromJsonAsync<BatchClosureCheck>($"api/batches/{batch.Batch.Id}/closure");
            if (check?.Status == BatchStatus.Closed) break;
            await Task.Delay(1000);
        }
        Require(check is { Status: BatchStatus.Closed, Closure: not null } && check.Closure.ArchiveId == archive,
            "Browser did not complete quality disposition and close the original archive within 15 minutes.");
        var closureAudit = check!.Closure!;
        using (var repeatClose = await quality.PostAsync($"api/batches/{batch.Batch.Id}/close", null))
        {
            Require(repeatClose.StatusCode == HttpStatusCode.OK, "Repeat close did not return the original audit.");
            Require(await repeatClose.Content.ReadFromJsonAsync<BatchClosureAudit>() == closureAudit,
                "Repeat close changed the original quality audit.");
        }
        var report = (await quality.GetFromJsonAsync<BatchOperationalReport>($"api/batches/{batch.Batch.Id}/report"))!;
        using var csvResponse = await quality.GetAsync($"api/batches/{batch.Batch.Id}/report.csv");
        csvResponse.EnsureSuccessStatusCode();
        var csv = await csvResponse.Content.ReadAsStringAsync();
        Require(report.Status == BatchStatus.Closed && report.Closure == closureAudit && report.Inspections.Count == 3
            && report.Summary.FirstInspection == new FirstInspectionYield(1, 0, 1, 0)
            && report.Summary.Purposes.Single(row => row.Purpose == InspectionPurpose.Reinspection).Attempts == 1
            && report.Summary.BySource.Single(row => row.SourceKind == "ConstructedNormal").FirstInspection.EvaluatedProducts == 0
            && report.Inspections.Single(row => row.Inspection.Id == original.Id).Quality.ReinspectionId == repeated.Id
            && report.Inspections.Single(row => row.Inspection.Id == repeated.Id).Quality.SourceReworkOrder?.OriginalInspectionId == original.Id
            && report.Inspections.Single(row => row.Inspection.Id == repeated.Id).Quality.Review?.Disposition == ReviewDisposition.Reject
            && csvResponse.Content.Headers.ContentDisposition?.DispositionType == "attachment"
            && csv.Contains(original.Id.ToString()) && csv.Contains(repeated.Id.ToString()),
            "Closed JSON/CSV report mixed first-pass yield, reinspection, constructed normal or immutable quality links.");
        await WriteLive(Path.Combine(context.Output, "closed-report.json"), report);
        await File.WriteAllTextAsync(Path.Combine(context.Output, "closed-report.csv"), csv);
        await VerifyLiveRecord(quality, store, original);
        await VerifyLiveRecord(quality, store, repeated);
        await model.RefreshActiveBatchCommand.ExecuteAsync(null);
        Require(store.ReadActiveBatch() is { Status: BatchStatus.Closed, Session: null }
            && !model.RunCommand.CanExecute(null), "WPF did not apply Closed and revoke the original execution session.");
        await SnapshotAsync(window, Path.Combine(output, "05-closed-batch-local.png"));
        using var created = await process.PostAsJsonAsync("api/batches", new CreateBatchRequest(
            "SIM-NEXT-" + Guid.NewGuid().ToString("N")[..8], "隔离模拟 PCB", "TOP-640x640", 1,
            context.Options.StationId, context.RecipeVersionId));
        Require(created.StatusCode == HttpStatusCode.Created, "Could not assign the next simulation batch.");
        var next = (await created.Content.ReadFromJsonAsync<BatchDetails>())!;
        await model.RefreshBatchesCommand.ExecuteAsync(null);
        model.SelectedBatch = model.AssignedBatches.Single(row => row.Summary.Batch.Id == next.Batch.Id);
        Require(model.DownloadBatchCommand.CanExecute(null), "Closed local batch still prevented downloading the next batch.");
        await model.DownloadBatchCommand.ExecuteAsync(null);
        Require(store.ReadActiveBatch() is { Status: BatchStatus.AwaitingFirstArticle, AcceptedProductionCount: 0, Session: null } active
            && active.Batch.Id == next.Batch.Id && active.ArchiveId != archive && model.RunCommand.CanExecute(null)
            && store.ReadRecent().Count == 3, "Next batch reused the old archive/counts or removed its historical inspections.");
        await SnapshotAsync(window, Path.Combine(output, "06-next-batch-first-article-ready.png"));
        await WriteLive(Path.Combine(context.Output, "closure-live-verified.json"), new { verifiedAt = DateTimeOffset.UtcNow,
            closed = check, next = store.ReadActiveBatch(), oldOriginalHash = InspectionTransfer.Hash(store.Get(original.Id)!),
            oldReinspectionHash = InspectionTransfer.Hash(store.Get(repeated.Id)!), pending = store.PendingCount(), wpfEvidence = output });
    }

    private static async Task VerifyLiveRecord(HttpClient quality, LocalInspectionStore store, InspectionRecord expected)
    {
        var central = (await quality.GetFromJsonAsync<LiveInspectionDetail>($"api/inspections/{expected.Id}"))!;
        var tested = await quality.GetByteArrayAsync($"api/inspections/{expected.Id}/images/tested");
        var reference = await quality.GetByteArrayAsync($"api/inspections/{expected.Id}/images/reference");
        var expectedHash = InspectionTransfer.Hash(expected);
        Require(central.ContentHash == expectedHash && tested.SequenceEqual(expected.TestedImage!)
            && reference.SequenceEqual(expected.ReferenceImage!)
            && InspectionTransfer.Hash(central.Inspection with { TestedImage = tested, ReferenceImage = reference }) == expectedHash
            && InspectionTransfer.Hash(store.Get(expected.Id)!) == expectedHash
            && store.ReadArchive(expected.Id)!.Receipt?.ContentHash == expectedHash,
            "Review or reinspection changed original SQL/SQLite metadata, image bytes or receipt hash.");
    }

    private static async Task VerifyFinalReworkReview(ReworkUiContext context, HttpClient quality)
    {
        var evidence = JsonSerializer.Deserialize<ReworkLiveResult>(await File.ReadAllTextAsync(
            Path.Combine(context.Output, "ready-for-browser.json")), LiveJson)!;
        var parent = (await quality.GetFromJsonAsync<InspectionQualityDetails>(
            $"api/quality/inspections/{evidence.OriginalInspectionId}"))!;
        var child = (await quality.GetFromJsonAsync<InspectionQualityDetails>(
            $"api/quality/inspections/{evidence.ReinspectionId}"))!;
        var queue = (await quality.GetFromJsonAsync<QualityQueuePage>($"api/quality/queue?batchId={evidence.BatchId}"))!;
        var batch = (await quality.GetFromJsonAsync<BatchDetails>($"api/batches/{evidence.BatchId}"))!;
        Require(parent.Review?.Disposition == ReviewDisposition.Rework && parent.ReinspectionId == evidence.ReinspectionId
            && child.Review is { Disposition: ReviewDisposition.Reject } && !string.IsNullOrWhiteSpace(child.Review.Note)
            && child.SourceReworkOrder?.Id == evidence.ReworkOrderId && child.ReworkOrder is null
            && queue.Total == 0 && batch.ReceivedProductionCount == 1,
            "Browser final rejection was not saved or did not resolve the correct reinspection queue item.");
        var store = new LocalInspectionStore(evidence.StationDatabase);
        var original = store.Get(evidence.OriginalInspectionId)!;
        var repeated = store.Get(evidence.ReinspectionId)!;
        Require(InspectionTransfer.Hash(original) == evidence.OriginalHash
            && InspectionTransfer.Hash(repeated) == evidence.ReinspectionHash,
            "Original local evidence changed after browser final review.");
        await VerifyLiveRecord(quality, store, original);
        await VerifyLiveRecord(quality, store, repeated);
        await using (var archiveModel = new StationViewModel(context.Options, null, null, false))
        {
            var window = new MainWindow { DataContext = archiveModel };
            window.Show();
            try
            {
                await archiveModel.InitializeAsync();
                archiveModel.SelectedHistory = archiveModel.History.Single(row => row.Id == repeated.Id);
                await archiveModel.ViewHistoryCommand.ExecuteAsync(null);
                Require(archiveModel.InspectionId == repeated.Id.ToString() && archiveModel.Decision == "缺陷"
                    && archiveModel.DefectOverlays.Count == repeated.Defects.Count && archiveModel.PendingCount == 0
                    && !archiveModel.RunCommand.CanExecute(null), "Anonymous reinspection archive lost its original images or machine decision.");
                await SnapshotAsync(window, Path.Combine(context.Output, "04-final-local-reinspection-archive.png"));
            }
            finally { window.Close(); }
        }
        await WriteLive(Path.Combine(context.Output, "final-review-verified.json"), new { verifiedAt = DateTimeOffset.UtcNow,
            evidence, parent, child, queue, batch, pending = store.PendingCount() });
    }
}
