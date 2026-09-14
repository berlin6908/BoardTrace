using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Smoke;

public static partial class Program
{
    private sealed record SystemStationContext(StationOptions Options, string AccountsPath, Guid BatchId,
        Guid RecipeVersionId, string BundleHash, string ModelSha256, int PlcPort, int ProductionCount,
        DateTimeOffset MinimumEndUtc, string ProductPrefix, string SamplesPath, string Output);

    private static readonly JsonSerializerOptions SystemJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions SystemLineJson = new(JsonSerializerDefaults.Web);

    private static async Task VerifySystemStationAsync(string contextPath)
    {
        var context = JsonSerializer.Deserialize<SystemStationContext>(await File.ReadAllTextAsync(contextPath), SystemJson)
            ?? throw new InvalidDataException("缺少双工位验收上下文。");
        Require(context.BatchId != Guid.Empty && context.RecipeVersionId != Guid.Empty && context.ProductionCount > 0
            && context.PlcPort is >= 1 and <= 65535 && context.BundleHash.Length == 64 && context.ModelSha256.Length == 64,
            "双工位验收上下文缺少固定批次、方案或触发数量。");
        Directory.CreateDirectory(context.Output);
        var stationStore = new LocalInspectionStore(context.Options.DatabasePath);
        Require(!File.Exists(context.Options.DatabasePath), "双工位验收只能使用新的工位 SQLite；恢复测试另存阶段上下文。");
        var login = await OpenLiveLogin(context.Options, context.AccountsPath);
        await using var model = new StationViewModel(context.Options, login.AuthenticatedUser, login.PersonnelSession, false);
        var window = new MainWindow { DataContext = model };
        window.Show();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await model.InitializeAsync();
            await model.RefreshBatchesCommand.ExecuteAsync(null);
            model.SelectedBatch = model.AssignedBatches.Single(choice => choice.Summary.Batch.Id == context.BatchId);
            var version = await login.PersonnelSession!.Client.GetFromJsonAsync<PublishedRecipeVersion>(
                $"api/recipes/versions/{context.RecipeVersionId}")
                ?? throw new InvalidDataException("中央未返回指定发布版本。");
            Require(string.Equals(version.BundleHash, context.BundleHash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(version.Bundle.Model?.Sha256, context.ModelSha256, StringComparison.OrdinalIgnoreCase),
                "发布包或模型 SHA-256 与验收固定身份不符。");
            await model.DownloadBatchCommand.ExecuteAsync(null);
            Require(model.ActiveRecipeIdentity == context.RecipeVersionId.ToString("D"), "工位未加载指定发布版本。");
            var active = stationStore.ReadActiveBatch() ?? throw new InvalidDataException("工位没有完整缓存批次。");
            Require(active.Batch.StationId == context.Options.StationId &&
                string.Equals(active.Batch.RecipeBundleHash, context.BundleHash, StringComparison.OrdinalIgnoreCase)
                && active.Batch.PlannedQuantity == context.ProductionCount && active.AcceptedProductionCount == 0,
                "批次工位、完整包哈希或计划数量不匹配；拒绝复用旧档案。 ");
            Require(active.Status is BatchStatus.AwaitingFirstArticle or BatchStatus.Approved,
                "验收批次必须是未生产的新批次。 ");

            if (active.Status == BatchStatus.AwaitingFirstArticle)
            {
                model.SelectedInputSource = "数据集回放";
                model.ConstructedNormal = true;
                model.ProductId = $"FIRST-{context.Options.StationId}";
                Require(model.RunCommand.CanExecute(null), "首件检测入口不可用：" + model.BatchNotice);
                await model.RunCommand.ExecuteAsync(null);
                var firstId = Guid.Parse(model.InspectionId);
                var first = stationStore.Get(firstId)!;
                Require(first.Purpose == InspectionPurpose.FirstArticle && first.Decision == QualityDecision.Pass
                    && first.ExecutionStatus == InspectionExecution.Completed && first.SourceKind == "ConstructedNormal",
                    "固定方案未将显式构造正常首件判为 Pass；不得伪造批准。 ");
                await WaitSystemAsync(() => stationStore.PendingCount() == 0, TimeSpan.FromMinutes(5), "首件上传回执");
                var accounts = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(context.AccountsPath), SystemJson)!;
                using var quality = StationAuthentication.CreatePersonnelSession(context.Options.ServerUrl);
                using (var signedIn = await quality.Client.PostAsJsonAsync("api/auth/login", new LoginRequest("quality", accounts["quality"])))
                    signedIn.EnsureSuccessStatusCode();
                using (var approval = await quality.Client.PutAsJsonAsync($"api/batches/{context.BatchId}/first-article-approval",
                    new ApproveFirstArticleRequest(firstId))) approval.EnsureSuccessStatusCode();
                await model.RefreshActiveBatchCommand.ExecuteAsync(null);
            }
            Require(model.StartBatchCommand.CanExecute(null), "当前批次未通过首件批准或人员启动门禁：" + model.BatchNotice);
            await model.StartBatchCommand.ExecuteAsync(null);
            model.ConstructedNormal = false;
            model.SelectedInputSource = "数据集回放";
            Require(model.RunCommand.CanExecute(null), "真实批次生产入口不可用：" + model.BatchNotice);
            model.PlcHost = "127.0.0.1";
            model.PlcPort = context.PlcPort.ToString();
            Require(model.StartPlcCommand.CanExecute(null), "PLC 通信入口不可用。 ");
            model.StartPlcCommand.Execute(null);
            await SnapshotAsync(window, Path.Combine(context.Output, "station-start.png"));
            await using var samples = new StreamWriter(Path.Combine(context.Output, "station-progress.jsonl")) { AutoFlush = true };
            var lastCount = -1L;
            var lastLogged = DateTimeOffset.MinValue;
            while (true)
            {
                var production = stationStore.ReadActiveBatch()?.AcceptedProductionCount
                    ?? throw new InvalidDataException("运行期间本地批次选择消失。");
                var pending = stationStore.PendingCount();
                var ackPending = stationStore.ReadUnacknowledgedPlc() is not null;
                if (production != lastCount || DateTimeOffset.UtcNow - lastLogged >= TimeSpan.FromSeconds(30))
                {
                    lastLogged = DateTimeOffset.UtcNow;
                    await samples.WriteLineAsync(JsonSerializer.Serialize(new { atUtc = lastLogged,
                        stationId = context.Options.StationId, production, pending, ackPending,
                        plcStatus = model.PlcStatus, processId = Environment.ProcessId }, SystemLineJson));
                    lastCount = production;
                }
                if (production == context.ProductionCount && pending == 0 && !ackPending
                    && DateTimeOffset.UtcNow >= context.MinimumEndUtc) break;
                Require(production <= context.ProductionCount && !model.PlcStatus.StartsWith("PLC 通信停止：", StringComparison.Ordinal),
                    "工位超过计划数量或 PLC 通信意外停止：" + model.PlcStatus);
                await Task.Delay(1000);
            }
            await model.StopPlcCommand.ExecuteAsync(null);
            await SnapshotAsync(window, Path.Combine(context.Output, "station-complete.png"));
            var audited = await ExportSystemRecordsAsync(context);
            var final = SystemCounts(context.Options.DatabasePath, context.BatchId);
            Require(final.Production == context.ProductionCount && final.Completed + final.Failed == final.Production
                && final.LastSequence == final.Production, "终态档案或生产序号不完整。");
            var report = new { scope = "双工位真实WPF/SQLite/Modbus验收；质量批准须由中央真实发布流程另行证明",
                context.Options.StationId, context.BatchId, context.RecipeVersionId, context.BundleHash, context.ModelSha256,
                processId = Environment.ProcessId, startedAt, completedAt = DateTimeOffset.UtcNow,
                production = final.Production, audited, completed = final.Completed, failed = final.Failed,
                lastSequence = final.LastSequence, pending = stationStore.PendingCount(),
                unacknowledged = stationStore.ReadUnacknowledgedPlc()?.Record.Id };
            await File.WriteAllTextAsync(Path.Combine(context.Output, "station-result.json"), JsonSerializer.Serialize(report, SystemJson));
        }
        finally
        {
            if (model.StopPlcCommand.CanExecute(null)) await model.StopPlcCommand.ExecuteAsync(null);
            window.Close();
        }
    }

    private static async Task<int> ExportSystemRecordsAsync(SystemStationContext context)
    {
        using var connection = new SqliteConnection($"Data Source={context.Options.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.Document,i.TestedImage,i.ReferenceImage,r.ContentHash,r.ReceivedAt,p.AckAt
            FROM Inspections i
            LEFT JOIN UploadReceipts r ON r.InspectionId=i.Id
            LEFT JOIN PlcTriggers p ON p.InspectionId=i.Id
            WHERE json_extract(i.Document,'$.batchId')=$batch AND json_extract(i.Document,'$.purpose')='Production'
            ORDER BY json_extract(i.Document,'$.productionSequence');
            """;
        command.Parameters.AddWithValue("$batch", context.BatchId.ToString("D"));
        using var reader = command.ExecuteReader();
        await using var output = new StreamWriter(Path.Combine(context.Output, "inspections.jsonl")) { AutoFlush = true };
        var count = 0;
        var products = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<InspectionIdentity>();
        while (reader.Read())
        {
            var record = JsonSerializer.Deserialize<InspectionRecord>(reader.GetString(0), SystemJson)! with
            {
                TestedImage = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1),
                ReferenceImage = reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2)
            };
            count++;
            Require(record.ProductionSequence == count && record.BatchId == context.BatchId &&
                record.StationId == context.Options.StationId && record.SourceKind == "Replay" &&
                record.ControllerSessionId is not null && record.TriggerSequence is not null &&
                products.Add(record.ProductId) &&
                identities.Add(new InspectionIdentity(record.ControllerSessionId!.Value, record.TriggerSequence!.Value)),
                "生产序号、工位、产品或PLC物理身份发生重复或串单。");
            Require(!reader.IsDBNull(3) && !reader.IsDBNull(4) && !reader.IsDBNull(5) &&
                reader.GetString(3) == InspectionTransfer.Hash(record),
                "中央回执、PLC ACK 或本地原图/结果哈希不完整。");
            if (record.ExecutionStatus == InspectionExecution.Completed)
                Require(record.TestedImage is { Length: > 0 } && record.ReferenceImage is { Length: > 0 },
                    "完成检测缺少原图或固定参考图。");
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                inspectionId = record.Id, record.BatchId, record.StationId, record.ProductId, record.SampleId,
                record.SourceKind, record.ExecutionStatus, record.Decision, record.ProductionSequence,
                record.ControllerSessionId, record.TriggerSequence, record.StartedAt, record.CompletedAt,
                record.DetectionMs, defectCount = record.Defects.Count,
                contentHash = reader.GetString(3), receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                ackAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                testedSha256 = record.TestedImage is null ? null : Convert.ToHexStringLower(SHA256.HashData(record.TestedImage)),
                referenceSha256 = record.ReferenceImage is null ? null : Convert.ToHexStringLower(SHA256.HashData(record.ReferenceImage))
            }, SystemLineJson));
        }
        Require(count == context.ProductionCount, "逐件档案数量不足或多于计划值。");
        return count;
    }

    private static (long Production, long Completed, long Failed, long LastSequence) SystemCounts(string database, Guid batchId)
    {
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
              COALESCE(SUM(CASE WHEN json_extract(Document,'$.executionStatus')='Completed' THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN json_extract(Document,'$.executionStatus') IN ('Failed','Interrupted') THEN 1 ELSE 0 END),0),
              COALESCE(MAX(json_extract(Document,'$.productionSequence')),0)
            FROM Inspections
            WHERE json_extract(Document,'$.batchId')=$batch AND json_extract(Document,'$.purpose')='Production';
            """;
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static async Task WaitSystemAsync(Func<bool> ready, TimeSpan timeout, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException(operation + "超时。 ");
            await Task.Delay(250);
        }
    }
}
