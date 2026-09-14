using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

namespace BoardTrace.Station.Tests;

public sealed class LocalRecipeStoreTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (LocalRecipeStore Store, PublishedRecipeVersion Version, Dictionary<Guid, byte[]> Assets) Setup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString());
        var store = new LocalRecipeStore(Path.Combine(folder, "station.db"));
        new LocalInspectionStore(store.DatabasePath).Initialize();
        store.Initialize();
        using var first = Image();
        using var second = Image();
        Cv2.Circle(second, new Point(520, 400), 20, Scalar.Black, -1);
        var assets = new Dictionary<Guid, byte[]> { [Guid.NewGuid()] = first.ToBytes(".png"), [Guid.NewGuid()] = second.ToBytes(".png") };
        var references = assets.Select((asset, index) => new PublishedRecipeReference($"sample-{index}", asset.Key,
            Hash(asset.Value), asset.Value.Length)).ToArray();
        var bundle = new PublishedRecipeBundle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Typed unit fixture",
            new ClassicalRecipeDefinition(new RecipeClassicalSettings(BoxPadding: 4)), new RecipeTargets(0.99, 0.99, 100), new RecipeTargets(0.99, 0.99, 100),
            new PublishedRecipeInput(640, 640, true),
            Hash(File.ReadAllBytes(typeof(ClassicalDetector).Assembly.Location)), new string('a', 64), new string('b', 64),
            references, null, "fixture-engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
        return (store, new PublishedRecipeVersion(bundle, PublishedRecipeTransfer.Hash(bundle)), assets);
    }

    private static Mat Image()
    {
        var image = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
        Cv2.Rectangle(image, new Rect(83, 65, 300, 35), Scalar.Black, -1);
        Cv2.Rectangle(image, new Rect(348, 65, 35, 440), Scalar.Black, -1);
        Cv2.Circle(image, new Point(200, 450), 60, Scalar.Black, -1);
        return image;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static PublishedRecipeVersion Version(PublishedRecipeBundle bundle) => new(bundle, PublishedRecipeTransfer.Hash(bundle));

    private static void Execute(LocalRecipeStore store, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Count(LocalRecipeStore store, string table)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void ReopenedCacheDetectsUsingFrozenReferenceAndExplicitSettings()
    {
        var (store, version, assets) = Setup();
        var reference = assets[version.Bundle.References[0].AssetId];
        var source = Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "source-reference.png");
        File.WriteAllBytes(source, reference);
        store.Save(version, assets);
        File.WriteAllBytes(source, [1, 2, 3]);
        File.Delete(source);
        assets[version.Bundle.References[0].AssetId] = [4, 5, 6];

        var loaded = new LocalRecipeStore(store.DatabasePath).Load(version.Bundle.VersionId);
        Assert.Equal(version.Bundle.VersionId, loaded.VersionId);
        Assert.Equal(version.BundleHash, JsonSerializer.Deserialize<PublishedRecipeVersion>(loaded.RecipeJson, Json)!.BundleHash);
        Assert.Equal("Pass", loaded.Detect("sample-0", reference).Decision);
        using var tested = Cv2.ImDecode(reference, ImreadModes.Grayscale);
        Cv2.Rectangle(tested, new Rect(470, 280, 15, 18), Scalar.Black, -1);
        var defect = Assert.Single(loaded.Detect("sample-0", tested.ToBytes(".png")).Defects);
        Assert.Equal(new double[] { 466, 276, 489, 302 }, defect.Box);
        Assert.Throws<InvalidDataException>(() => loaded.Detect("unknown", reference));
        Assert.Throws<InvalidDataException>(() => loaded.Detect("sample-0", [1, 2, 3]));
    }

    [Fact]
    public void PartialAssetInsertRollsBackWholeNewVersionAndKeepsPreviousVersion()
    {
        var (store, version, assets) = Setup();
        store.Save(version, assets);
        var next = Version(version.Bundle with { VersionId = Guid.NewGuid(), Name = "next fixture" });
        Execute(store, $"""
            CREATE TRIGGER reject_second_asset BEFORE INSERT ON RecipeAssets
            WHEN NEW.VersionId='{next.Bundle.VersionId}' AND (SELECT COUNT(*) FROM RecipeAssets WHERE VersionId=NEW.VersionId)=1
            BEGIN SELECT RAISE(ABORT, 'simulated full disk'); END;
            """);
        Assert.Throws<SqliteException>(() => store.Save(next, assets));
        Assert.Equal(1, Count(store, "Recipes"));
        Assert.Equal(2, Count(store, "RecipeAssets"));
        Assert.Throws<KeyNotFoundException>(() => store.Load(next.Bundle.VersionId));
        Assert.Equal("Pass", store.Load(version.Bundle.VersionId).Detect("sample-0", assets[version.Bundle.References[0].AssetId]).Decision);
    }

    [Fact]
    public void ExistingVersionCannotChangeContentAndIdenticalSaveIsIdempotent()
    {
        var (store, version, assets) = Setup();
        store.Save(version, assets);
        store.Save(version, assets);
        Assert.Throws<InvalidOperationException>(() => store.Save(Version(version.Bundle with { Name = "replaced" }), assets));
        Assert.Equal(1, Count(store, "Recipes"));
        Assert.Equal(2, Count(store, "RecipeAssets"));
        var stored = JsonSerializer.Deserialize<PublishedRecipeVersion>(store.Load(version.Bundle.VersionId).RecipeJson, Json)!;
        Assert.Equal(version.Bundle.Name, stored.Bundle.Name);
    }

    [Fact]
    public void WrongBundleHashOrIncompleteOrCorruptAssetsNeverEnterDatabase()
    {
        var (store, version, assets) = Setup();
        Assert.Throws<InvalidDataException>(() => store.Save(version with { BundleHash = new string('0', 64) }, assets));
        var id = version.Bundle.References[0].AssetId;
        var corrupt = assets.ToDictionary();
        corrupt[id] = assets[id].ToArray();
        corrupt[id][0] ^= 1;
        Assert.Throws<InvalidDataException>(() => store.Save(version, corrupt));
        corrupt[id] = assets[id][..^1];
        Assert.Throws<InvalidDataException>(() => store.Save(version, corrupt));
        corrupt.Remove(id);
        Assert.Throws<InvalidDataException>(() => store.Save(version, corrupt));
        Assert.Equal(0, Count(store, "Recipes"));
        Assert.Equal(0, Count(store, "RecipeAssets"));
    }

    [Theory]
    [InlineData("DELETE FROM RecipeAssets WHERE rowid=(SELECT MIN(rowid) FROM RecipeAssets)")]
    [InlineData("UPDATE RecipeAssets SET Content=X'010203' WHERE rowid=(SELECT MIN(rowid) FROM RecipeAssets)")]
    [InlineData("UPDATE Recipes SET BundleHash='tampered'")]
    [InlineData("UPDATE Recipes SET Document=json_set(Document,'$.bundle.name','tampered')")]
    public void MissingOrTamperedCacheCannotLoad(string damage)
    {
        var (store, version, assets) = Setup();
        store.Save(version, assets);
        Execute(store, damage);
        Assert.Throws<InvalidDataException>(() => store.Load(version.Bundle.VersionId));
    }

    [Fact]
    public void UnsupportedAlgorithmInputAndDifferentAssemblyAreRejected()
    {
        var (store, version, assets) = Setup();
        Assert.Throws<InvalidDataException>(() => store.Save(Version(version.Bundle with
        { Definition = new PairedOnnxRecipeDefinition(new string('a', 64), new(0.5, 0.5, 0.5, 0.5, 0.5, 0.5)) }), assets));
        Assert.Throws<InvalidDataException>(() => store.Save(Version(version.Bundle with { Input = new PublishedRecipeInput(320, 320, false) }), assets));
        var differentAssembly = Version(version.Bundle with { AlgorithmAssemblySha256 = new string('0', 64) });
        store.Save(differentAssembly, assets);
        Assert.Throws<InvalidDataException>(() => store.Load(differentAssembly.Bundle.VersionId));
    }

    [Fact]
    public void SharedAssetCanServeTwoSamplesWithoutDuplicateBlob()
    {
        var (store, version, assets) = Setup();
        var first = version.Bundle.References[0];
        var shared = Version(version.Bundle with { References = [first, first with { SampleId = "another-sample" }] });
        store.Save(shared, new Dictionary<Guid, byte[]> { [first.AssetId] = assets[first.AssetId] });
        Assert.Equal(1, Count(store, "RecipeAssets"));
        Assert.Equal("Pass", store.Load(shared.Bundle.VersionId).Detect("another-sample", assets[first.AssetId]).Decision);
    }

    [Fact]
    public void CorrectHashDoesNotMakeAnUndecodableReferenceLoadable()
    {
        var (store, version, _) = Setup();
        byte[] badImage = [1, 2, 3];
        var reference = version.Bundle.References[0] with { Sha256 = Hash(badImage), ByteLength = badImage.Length };
        var bad = Version(version.Bundle with { References = [reference] });
        store.Save(bad, new Dictionary<Guid, byte[]> { [reference.AssetId] = badImage });
        Assert.Throws<InvalidDataException>(() => store.Load(bad.Bundle.VersionId));
    }

    [Fact]
    public async Task FailedSecondDownloadDoesNotPersistAnyPartOfNewVersion()
    {
        var (store, version, assets) = Setup();
        store.Save(version, assets);
        var next = Version(version.Bundle with { VersionId = Guid.NewGuid() });
        var assetRequests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/bundle"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(next) };
            Assert.Equal(1, Count(store, "Recipes"));
            Assert.Equal(2, Count(store, "RecipeAssets"));
            if (++assetRequests == 2) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var assetId = Guid.Parse(request.RequestUri.Segments[^1]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(assets[assetId]) };
        })) { BaseAddress = new Uri("http://fixture.local/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => new PublishedRecipeDownloader(store, client).DownloadAsync(next.Bundle.VersionId));
        Assert.Equal(2, assetRequests);
        Assert.Equal(1, Count(store, "Recipes"));
        Assert.Equal(2, Count(store, "RecipeAssets"));
        Assert.Throws<KeyNotFoundException>(() => store.Load(next.Bundle.VersionId));
        Assert.Equal(version.Bundle.VersionId, store.Load(version.Bundle.VersionId).VersionId);
    }

    [Fact]
    public async Task DownloadedSharedAssetLoadsAndDetectsWithoutFurtherHttpRequests()
    {
        var (store, version, assets) = Setup();
        var reference = version.Bundle.References[0];
        var shared = Version(version.Bundle with { References = [reference, reference with { SampleId = "shared" }] });
        var assetRequests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == $"/api/recipes/versions/{shared.Bundle.VersionId:D}/bundle")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(shared) };
            Assert.Equal($"/api/recipe-assets/{reference.AssetId:D}", request.RequestUri.AbsolutePath);
            Assert.Equal(0, Count(store, "Recipes"));
            assetRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(assets[reference.AssetId]) };
        })) { BaseAddress = new Uri("http://fixture.local/") };
        var loaded = await new PublishedRecipeDownloader(store, client).DownloadAsync(shared.Bundle.VersionId);
        client.Dispose();
        Assert.Equal(1, assetRequests);
        Assert.Equal("Pass", loaded.Detect("shared", assets[reference.AssetId]).Decision);
        Assert.Equal(1, Count(store, "RecipeAssets"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
