using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;

internal sealed record ProbeFixture(ReplaySample Sample, byte[] Reference, byte[] Tested, LocalInspectionStore Store,
    PublishedRecipeVersion Version, BatchDefinition Batch, InspectionCoordinator Coordinator)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);
    internal static readonly CurrentUser Actor = new("fixture-operator", "operator", "Isolated PLC Operator", ["Operator"], null);
    internal const string Scope = "隔离本地批准批次夹具，非质量发布；真实 Python TCP/Runner/Core/OpenCV/SQLite。未读 truth/test，未连接中央。";
    internal static async Task<ProbeFixture> CreateAsync(string root, string folder, string scenario)
    {
        Directory.CreateDirectory(folder);
        var inputFile = Path.Combine(root, "training/manifests/inputs/validation.jsonl");
        var inputLine = File.ReadLines(inputFile).First();
        var sample = JsonSerializer.Deserialize<ReplaySample>(inputLine, Json)!;
        using var input = JsonDocument.Parse(inputLine);
        var reference = await File.ReadAllBytesAsync(Path.Combine(root, "data", sample.Reference));
        var tested = await File.ReadAllBytesAsync(Path.Combine(root, "data", sample.Image));
        Require(Hash(reference) == input.RootElement.GetProperty("referenceSha256").GetString(), "Reference SHA.");
        Require(Hash(tested) == input.RootElement.GetProperty("imageSha256").GetString(), "Validation image SHA.");
        var store = new LocalInspectionStore(Path.Combine(folder, "station.db")); store.Initialize();
        Require(store.ReadRecent().Count == 0, "Use a new evidence directory; never overwrite earlier runs.");
        var recipes = new LocalRecipeStore(store.DatabasePath); recipes.Initialize();
        var versionId = Guid.NewGuid(); var assetId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var targets = new RecipeTargets(1, 1, 500);
        var bundle = new PublishedRecipeBundle(versionId, Guid.NewGuid(), Guid.NewGuid(), "隔离PLC集成夹具·非质量发布",
            new ClassicalRecipeDefinition(new RecipeClassicalSettings(BoxPadding: 4)), targets, targets, new(640, 640, true),
            Hash(await File.ReadAllBytesAsync(typeof(ClassicalDetector).Assembly.Location)), Hash(await File.ReadAllBytesAsync(inputFile)),
            new string('a', 64), [new(sample.SampleId, assetId, Hash(reference), reference.Length)], null, "fixture-engineer", "Isolated Fixture Engineer", now);
        var version = new PublishedRecipeVersion(bundle, PublishedRecipeTransfer.Hash(bundle));
        recipes.Save(version, new Dictionary<Guid, byte[]> { [assetId] = reference });
        var loaded = recipes.Load(versionId);
        var batch = new BatchDefinition(Guid.NewGuid(), "SIM-PLC-" + scenario.ToUpperInvariant(), "PCB", "TOP", 4,
            "PLC-INTEGRATION-01", versionId, version.BundleHash, "fixture-engineer", "Isolated Fixture Engineer", now);
        var coordinator = new InspectionCoordinator(store, new ClassicalSettings());
        coordinator.UseBatch(new(batch, BatchStatus.AwaitingFirstArticle, null, version), loaded);
        var first = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, batch.StationId, "SIM-FIRST-NORMAL", Actor,
            new PublishedReplayImageSource(Path.Combine(root, "data"), sample, reference));
        Require(first.ExecutionStatus == InspectionExecution.Completed && first.Decision == QualityDecision.Pass, "Real constructed-normal first article.");
        var approval = new FirstArticleApproval(batch.Id, first.Id, "fixture-quality", "Isolated Fixture Approval", DateTimeOffset.UtcNow);
        coordinator.UseBatch(new(batch, BatchStatus.Approved, approval, version), loaded);
        coordinator.StartBatch(new(Guid.NewGuid(), batch.Id, batch.StationId, batch.RecipeBundleHash, first.Id,
            Actor.Id, Actor.DisplayName, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), store.ReadActiveBatch()!.ArchiveId), null);
        await Write(folder, "fixture.json", new { scope = Scope, batch, approval, version, sample });
        await File.WriteAllTextAsync(Path.Combine(folder, "samples.jsonl"), JsonSerializer.Serialize(new { sample.SampleId }, LineJson) + "\n");
        return new(sample, reference, tested, store, version, batch, coordinator);
    }
    private static Task Write<T>(string folder, string name, T value) => File.WriteAllTextAsync(Path.Combine(folder, name), JsonSerializer.Serialize(value, Json));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
