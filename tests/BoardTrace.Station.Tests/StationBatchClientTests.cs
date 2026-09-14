using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using OpenCvSharp;

namespace BoardTrace.Station.Tests;

public sealed class StationBatchClientTests
{
    [Fact]
    public async Task LoginPrecedesListAndHttpFailureNeverAppearsAsAnEmptyAssignment()
    {
        var fixture = Fixture.Create();
        var requests = new List<string>();
        var fail = false;
        using var http = Client((request, _) =>
        {
            requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.Method == HttpMethod.Post ? LoggedIn() : fail
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json(new[] { new BatchSummary(fixture.Package.Batch, BatchStatus.AwaitingFirstArticle, 0) }));
        });
        var client = new StationBatchClient(http, new("fixture-device", "fixture-password"), "STATION-A");
        Assert.Equal(fixture.Package.Batch.Id, Assert.Single(await client.GetAssignedAsync()).Batch.Id);
        Assert.Equal(new[] { "POST /api/auth/login", "GET /api/station/batches" }, requests);
        fail = true;
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAssignedAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
    }

    [Fact]
    public async Task DownloadLoadsRealReferenceButDoesNotSelectAnActiveBatch()
    {
        var fixture = Fixture.Create();
        var requests = new List<string>();
        using var http = Client((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            return Task.FromResult(path switch
            {
                "/api/auth/login" => LoggedIn(),
                _ when path.EndsWith("/package") => Json(fixture.Package),
                _ when path.EndsWith("/bundle") => Json(fixture.Package.Recipe),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Image) }
            });
        });
        var downloaded = await new StationBatchClient(http, new("fixture-device", "fixture-password"), "STATION-A")
            .DownloadAsync(fixture.Package.Batch.Id, fixture.Store);
        Assert.Equal(fixture.Package.Batch, downloaded.Package.Batch);
        Assert.Equal(fixture.Package.Recipe.BundleHash, downloaded.Recipe.BundleHash);
        Assert.Equal("Pass", downloaded.Recipe.Detect("sample", fixture.Image).Decision);
        Assert.Equal("/api/auth/login", requests[0]);
        Assert.Equal(4, requests.Count);
        Assert.Null(fixture.Inspections.ReadActiveBatch());
        Assert.Equal(downloaded.Recipe.VersionId, fixture.Store.Load(downloaded.Recipe.VersionId).VersionId);
    }

    [Theory]
    [InlineData("station")]
    [InlineData("batch")]
    [InlineData("version")]
    [InlineData("hash")]
    public async Task MismatchedPackageIsRejectedBeforeAssetsOrCacheAreTouched(string mismatch)
    {
        var fixture = Fixture.Create();
        var batch = fixture.Package.Batch;
        var changed = mismatch switch
        {
            "station" => batch with { StationId = "STATION-B" },
            "batch" => batch with { Id = Guid.NewGuid() },
            "version" => batch with { RecipeVersionId = Guid.NewGuid() },
            _ => batch with { RecipeBundleHash = new string('0', 64) }
        };
        using var http = Client((request, _) =>
        {
            Assert.True(request.RequestUri!.AbsolutePath == "/api/auth/login" || request.RequestUri.AbsolutePath.EndsWith("/package"));
            return Task.FromResult(request.Method == HttpMethod.Post ? LoggedIn() : Json(fixture.Package with { Batch = changed }));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new StationBatchClient(http, new("fixture-device", "fixture-password"), "STATION-A")
            .DownloadAsync(batch.Id, fixture.Store));
        Assert.Throws<KeyNotFoundException>(() => fixture.Store.Load(batch.RecipeVersionId));
        Assert.Null(fixture.Inspections.ReadActiveBatch());
    }

    [Fact]
    public async Task CancellationDuringDownloadLeavesNoCachedOrActiveBatch()
    {
        var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        using var http = Client(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/auth/login") return LoggedIn();
            if (path.EndsWith("/package")) return Json(fixture.Package);
            if (path.EndsWith("/bundle")) return Json(fixture.Package.Recipe);
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Canceled request should not finish.");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StationBatchClient(http, new("fixture-device", "fixture-password"), "STATION-A")
            .DownloadAsync(fixture.Package.Batch.Id, fixture.Store, cancellation.Token));
        Assert.Throws<KeyNotFoundException>(() => fixture.Store.Load(fixture.Package.Batch.RecipeVersionId));
        Assert.Null(fixture.Inspections.ReadActiveBatch());
    }

    private static HttpResponseMessage Json<T>(T content) => new(HttpStatusCode.OK) { Content = JsonContent.Create(content) };
    private static HttpResponseMessage LoggedIn() => Json(new CurrentUser("fixture-device", "fixture-device", "Fixture Station", ["Station"], "STATION-A"));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => new(new Handler(respond)) { BaseAddress = new("http://fixture.local/") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }

    private sealed record Fixture(LocalInspectionStore Inspections, LocalRecipeStore Store, BatchPackage Package, byte[] Image)
    {
        public static Fixture Create()
        {
            var path = Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString("N"), "station.db");
            var inspections = new LocalInspectionStore(path); inspections.Initialize();
            var store = new LocalRecipeStore(path); store.Initialize();
            using var image = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
            Cv2.Rectangle(image, new Rect(83, 65, 300, 35), Scalar.Black, -1);
            Cv2.Rectangle(image, new Rect(348, 65, 35, 440), Scalar.Black, -1);
            Cv2.Circle(image, new Point(200, 450), 60, Scalar.Black, -1);
            var bytes = image.ImEncode(".png");
            var targets = new RecipeTargets(1, 1, 500);
            var bundle = new PublishedRecipeBundle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Isolated client fixture", "Classical", new(), targets, targets, new(640, 640, true),
                Hash(File.ReadAllBytes(typeof(ClassicalDetector).Assembly.Location)), new string('a', 64), new string('b', 64),
                [new("sample", Guid.NewGuid(), Hash(bytes), bytes.Length)], "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
            var version = new PublishedRecipeVersion(bundle, PublishedRecipeTransfer.Hash(bundle));
            var batch = new BatchDefinition(Guid.NewGuid(), "ISOLATED-CLIENT", "PCB", "TOP", 2, "STATION-A", bundle.VersionId, version.BundleHash, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
            return new(inspections, store, new(batch, BatchStatus.AwaitingFirstArticle, null, version), bytes);
        }
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
