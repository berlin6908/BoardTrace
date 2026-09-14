using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

namespace BoardTrace.Station.Tests;

public sealed class PairedRecipeTests
{
    private static readonly CurrentUser Actor = new("operator", "operator", "Fixture Operator", ["Operator"], null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Fixture Setup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString("N"));
        var store = new LocalInspectionStore(Path.Combine(folder, "station.db")); store.Initialize();
        var recipes = new LocalRecipeStore(store.DatabasePath); recipes.Initialize();
        var reference = Image(192, 32); var tested = Image(64, 200);
        var model = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pixel-detector.onnx"));
        var modelAsset = new PublishedRecipeModel(Guid.NewGuid(), Hash(model), model.Length, RecipeModelInput.PairedGrayAbsDiff640V1);
        var referenceAsset = new PublishedRecipeReference("pixel", Guid.NewGuid(), Hash(reference), reference.Length);
        var targets = new RecipeTargets(1, 1, 1000);
        var bundle = new PublishedRecipeBundle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "隔离 ONNX 像素夹具 · 非质量发布",
            new PairedOnnxRecipeDefinition(modelAsset.Sha256, new(0, 0, 0, 0, 0, 0)), targets, targets, new(640, 640, true),
            Hash(File.ReadAllBytes(typeof(OnnxDetector).Assembly.Location)), new string('a', 64), new string('b', 64),
            [referenceAsset], modelAsset, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
        return new(store, recipes, Version(bundle), new() { [referenceAsset.AssetId] = reference, [modelAsset.AssetId] = model }, tested);
    }

    private static byte[] Image(byte first, byte second)
    {
        using var image = new Mat(640, 640, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(image, new Rect(320, 0, 320, 640), Scalar.White, -1);
        image.Set(0, 0, first); image.Set(0, 1, second);
        return image.ToBytes(".png");
    }

    [Fact]
    public async Task CompleteDownloadAndColdCacheRunThePairedPlanesAfterAllSourceBytesAreGone()
    {
        var f = Setup(); var requests = new List<Guid>();
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/bundle")) return Response(f.Version);
            var id = Guid.Parse(request.RequestUri.Segments[^1]); requests.Add(id);
            Assert.Empty(f.Recipes.List()); // Neither reference nor model is visible before the full download commits.
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(f.Assets[id]) };
        })) { BaseAddress = new("http://fixture.local/") };
        using (var downloaded = await new PublishedRecipeDownloader(f.Recipes, http).DownloadAsync(f.Version.Bundle.VersionId))
            Assert.Equal(6, downloaded.Detect("pixel", f.Tested).Defects.Count);
        Assert.Equal(2, requests.Count);
        Assert.Contains(f.Version.Bundle.Model!.AssetId, requests);
        var reference = f.Reference.ToArray();
        f.Assets.Clear(); http.Dispose();
        using var loaded = new LocalRecipeStore(f.Store.DatabasePath).Load(f.Version.Bundle.VersionId);
        Assert.Equal("PairedOnnx", Assert.Single(f.Recipes.List()).Algorithm);
        Assert.Equal(reference, loaded.GetReferenceBytes("pixel"));
        Assert.Throws<InvalidDataException>(() => loaded.Detect("pixel", [1, 2, 3]));
        var result = loaded.Detect("pixel", f.Tested);
        Assert.Equal(Enumerable.Range(1, 6), result.Defects.Select(defect => defect.ClassId!.Value));
        Assert.Equal(new double[] { 64f / 255, 192f / 255, 128f / 255, 200f / 255, 32f / 255, 168f / 255 }, result.Defects.Select(defect => defect.Score));
        Assert.Throws<OperationCanceledException>(() => loaded.Detect("pixel", f.Tested, new CancellationToken(true)));
        Assert.Equal(result.Defects.Select(defect => defect.Score), loaded.Detect("pixel", f.Tested).Defects.Select(defect => defect.Score));
    }

    [Fact]
    public void FailedModelInsertRollsBackTheReferenceAndPackageTogether()
    {
        var f = Setup();
        Execute(f, $"CREATE TRIGGER fail_model BEFORE INSERT ON RecipeAssets WHEN NEW.AssetId='{f.Version.Bundle.Model!.AssetId}' BEGIN SELECT RAISE(ABORT,'fixture full disk'); END;");
        Assert.Throws<SqliteException>(() => f.Recipes.Save(f.Version, f.Assets));
        Assert.Empty(f.Recipes.List());
        Assert.Equal(0, ReadCount(f, "SELECT COUNT(*) FROM RecipeAssets"));
        Execute(f, "DROP TRIGGER fail_model;");
        f.Recipes.Save(f.Version, f.Assets);
        using var loaded = f.Recipes.Load(f.Version.Bundle.VersionId);
        Assert.Equal(6, loaded.Detect("pixel", f.Tested).Defects.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("changed")]
    public void MissingOrDamagedModelCannotBeSavedOrColdLoaded(string damage)
    {
        var f = Setup(); var modelId = f.Version.Bundle.Model!.AssetId;
        var damaged = f.Assets.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        if (damage == "missing") damaged.Remove(modelId);
        else if (damage == "truncated") damaged[modelId] = damaged[modelId][..^1];
        else damaged[modelId][0] ^= 1;
        Assert.Throws<InvalidDataException>(() => f.Recipes.Save(f.Version, damaged));
        Assert.Empty(f.Recipes.List());
        f.Recipes.Save(f.Version, f.Assets);
        Execute(f, damage == "missing" ? $"DELETE FROM RecipeAssets WHERE AssetId='{modelId}'"
            : $"UPDATE RecipeAssets SET Content=X'010203' WHERE AssetId='{modelId}'");
        Assert.Throws<InvalidDataException>(() => f.Recipes.Load(f.Version.Bundle.VersionId));
    }

    [Fact]
    public void ModelContractAndDefinitionMustAgreeAndInvalidOnnxDoesNotBecomeClassical()
    {
        var f = Setup(); var bundle = f.Version.Bundle;
        Assert.Throws<InvalidDataException>(() => f.Recipes.Save(Version(bundle with { Definition = new ClassicalRecipeDefinition(new()) }), f.Assets));
        Assert.Throws<InvalidDataException>(() => f.Recipes.Save(Version(bundle with { Model = bundle.Model! with { InputContract = "RGB" } }), f.Assets));
        Assert.Throws<InvalidDataException>(() => f.Recipes.Save(Version(bundle with { Definition = new PairedOnnxRecipeDefinition(new string('0', 64), new(0, 0, 0, 0, 0, 0)) }), f.Assets));
        var missingThreshold = JsonSerializer.Serialize(f.Version, Json).Replace("\"mousebite\":0,", "");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PublishedRecipeVersion>(missingThreshold, Json));
        byte[] invalid = [1, 2, 3];
        var bad = Version(bundle with { Definition = new PairedOnnxRecipeDefinition(Hash(invalid), new(0, 0, 0, 0, 0, 0)),
            Model = bundle.Model! with { Sha256 = Hash(invalid), ByteLength = invalid.Length } });
        f.Assets[bundle.Model!.AssetId] = invalid;
        f.Recipes.Save(bad, f.Assets);
        Assert.Throws<InvalidDataException>(() => f.Recipes.Load(bad.Bundle.VersionId));
    }

    [Fact]
    public async Task SameVersionMetadataRefreshReusesOwnedSessionAndShutdownReleasesIt()
    {
        var f = Setup(); f.Recipes.Save(f.Version, f.Assets);
        var loaded = f.Recipes.Load(f.Version.Bundle.VersionId);
        using var coordinator = new InspectionCoordinator(f.Store, new());
        var package = Package(f);
        coordinator.UseBatch(package, loaded);
        var first = await coordinator.InspectAsync(InspectionPurpose.FirstArticle, "STATION-A", "FIRST", Actor, new Source(f.Tested));
        var originalHash = InspectionTransfer.Hash(first);
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/login" => Response(new CurrentUser("device", "device", "Device", ["Station"], "STATION-A")),
            var path when path.EndsWith("/package") => Response(package),
            _ => throw new InvalidOperationException("Metadata refresh must never request model or reference bytes.")
        })) { BaseAddress = new("http://fixture.local/") };
        var refreshed = await new StationBatchClient(http, new("device", "fixture"), "STATION-A").GetPackageAsync(package.Batch.Id);
        coordinator.UseBatch(refreshed, loaded);
        Assert.Equal(6, loaded.Detect("pixel", f.Tested).Defects.Count);
        Assert.Equal(originalHash, InspectionTransfer.Hash(f.Store.Get(first.Id)!));
        coordinator.Dispose(); coordinator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.Detect("pixel", f.Tested));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.InspectAsync(InspectionPurpose.FirstArticle, "STATION-A", "AFTER", Actor, new Source(f.Tested)));
        Assert.Single(f.Store.ReadRecent());
    }

    [Fact]
    public async Task BusyOrRejectedSwitchKeepsPreviousSessionUntilSuccessfulHandover()
    {
        var f = Setup(); f.Recipes.Save(f.Version, f.Assets);
        var old = f.Recipes.Load(f.Version.Bundle.VersionId);
        using var candidate = f.Recipes.Load(f.Version.Bundle.VersionId);
        using var coordinator = new InspectionCoordinator(f.Store, new());
        coordinator.UsePublishedRecipe(old);
        var paused = new PausedSource(f.Tested);
        var running = coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "STATION-A", "HELD", Actor, paused,
            identity: new(Guid.NewGuid(), 1));
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Throws<InvalidOperationException>(() => coordinator.UsePublishedRecipe(candidate));
            Assert.Throws<InvalidOperationException>(coordinator.Dispose);
        }
        finally { paused.Release.SetResult(); }
        var completed = await running;
        Assert.Throws<InspectionRejectedException>(() => coordinator.UsePublishedRecipe(candidate));
        Assert.Equal(6, candidate.Detect("pixel", f.Tested).Defects.Count); // Failed transfer leaves caller ownership intact.
        Assert.Equal(6, old.Detect("pixel", f.Tested).Defects.Count);
        coordinator.ConfirmPlcAck(completed.Id);
        coordinator.UsePublishedRecipe(candidate);
        Assert.Throws<ObjectDisposedException>(() => old.Detect("pixel", f.Tested));
        Assert.Equal(6, candidate.Detect("pixel", f.Tested).Defects.Count);
        coordinator.UseDevelopmentRecipe(new());
        Assert.Throws<ObjectDisposedException>(() => candidate.Detect("pixel", f.Tested));
    }

    [Fact]
    public void UnserializableDevelopmentSettingsCannotDetachTheOwnedModelOrLeaveItsBatch()
    {
        var f = Setup(); f.Recipes.Save(f.Version, f.Assets);
        var loaded = f.Recipes.Load(f.Version.Bundle.VersionId);
        using var coordinator = new InspectionCoordinator(f.Store, new());
        coordinator.UseBatch(Package(f), loaded);
        var before = f.Store.ReadActiveBatch();
        Assert.Throws<ArgumentException>(() => coordinator.UseDevelopmentRecipe(new(MinimumAlignmentResponse: double.NaN)));
        Assert.Equal(before, f.Store.ReadActiveBatch());
        Assert.Equal(6, loaded.Detect("pixel", f.Tested).Defects.Count);
        coordinator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.Detect("pixel", f.Tested));
    }

    private static BatchPackage Package(Fixture f) => new(new(Guid.NewGuid(), "SIM-PAIRED", "PCB", "TOP", 1, "STATION-A",
        f.Version.Bundle.VersionId, f.Version.BundleHash, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow), BatchStatus.AwaitingFirstArticle, null, f.Version);
    private static PublishedRecipeVersion Version(PublishedRecipeBundle bundle) => new(bundle, PublishedRecipeTransfer.Hash(bundle));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static HttpResponseMessage Response<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static void Execute(Fixture f, string sql) { using var connection = new SqliteConnection($"Data Source={f.Store.DatabasePath}"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static long ReadCount(Fixture f, string sql) { using var connection = new SqliteConnection($"Data Source={f.Store.DatabasePath}"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
    private sealed record Fixture(LocalInspectionStore Store, LocalRecipeStore Recipes, PublishedRecipeVersion Version, Dictionary<Guid, byte[]> Assets, byte[] Tested)
    { public byte[] Reference => Assets[Version.Bundle.References[0].AssetId]; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request)); }
    private class Source(byte[] tested) : IImageSource
    { public string SampleId => "pixel"; public string SourceKind => "Replay"; public virtual Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new CapturedPair(tested, null)); }
    private sealed class PausedSource(byte[] tested) : Source(tested)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken)
        { Entered.SetResult(); await Release.Task.WaitAsync(cancellationToken); return await base.CaptureAsync(cancellationToken); }
    }
}
