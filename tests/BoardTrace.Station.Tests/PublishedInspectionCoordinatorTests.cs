using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;
using BoardTrace.Vision;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

namespace BoardTrace.Station.Tests;

public sealed class PublishedInspectionCoordinatorTests
{
    private static readonly CurrentUser Operator = new("fixture-operator", "fixture", "Fixture Operator", ["Operator"], null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static Fixture Setup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "boardtrace-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        using var reference = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
        Cv2.Rectangle(reference, new Rect(83, 65, 300, 35), Scalar.Black, -1);
        Cv2.Rectangle(reference, new Rect(348, 65, 35, 440), Scalar.Black, -1);
        Cv2.Circle(reference, new Point(200, 450), 60, Scalar.Black, -1);
        using var tested = reference.Clone();
        Cv2.Rectangle(tested, new Rect(470, 280, 15, 18), Scalar.Black, -1);
        var referenceBytes = reference.ToBytes(".png");
        var testedBytes = tested.ToBytes(".png");
        File.WriteAllBytes(Path.Combine(folder, "reference.png"), referenceBytes);
        File.WriteAllBytes(Path.Combine(folder, "tested.png"), testedBytes);
        var store = new LocalInspectionStore(Path.Combine(folder, "station.db"));
        store.Initialize();
        var recipes = new LocalRecipeStore(store.DatabasePath);
        recipes.Initialize();
        var referenceAsset = new PublishedRecipeReference("controlled-1", Guid.NewGuid(), Hash(referenceBytes), referenceBytes.Length);
        var bundle = new PublishedRecipeBundle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Unit fixture version",
            new ClassicalRecipeDefinition(new RecipeClassicalSettings(BoxPadding: 4)), new RecipeTargets(0.99, 0.99, 100), new RecipeTargets(0.99, 0.99, 100),
            new PublishedRecipeInput(640, 640, true), Hash(File.ReadAllBytes(typeof(ClassicalDetector).Assembly.Location)),
            new string('a', 64), new string('b', 64), [referenceAsset], null, "engineer", "Fixture Engineer", DateTimeOffset.UtcNow);
        var version = new PublishedRecipeVersion(bundle, PublishedRecipeTransfer.Hash(bundle));
        recipes.Save(version, new Dictionary<Guid, byte[]> { [referenceAsset.AssetId] = referenceBytes });
        return new Fixture(folder, store, recipes, recipes.Load(bundle.VersionId), version, referenceBytes, testedBytes);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static ReplaySample Sample(string id = "controlled-1") => new(id, "tested.png", "reference.png");

    [Fact]
    public async Task PublishedInspectionArchivesActualCachedReferenceAndFullVersionAfterSourceReferenceDeletion()
    {
        var f = Setup();
        File.Delete(Path.Combine(f.Folder, "reference.png"));
        var exposedCopy = f.Loaded.GetReferenceBytes("controlled-1");
        exposedCopy[0] ^= 1;
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        coordinator.UsePublishedRecipe(f.Loaded);
        var record = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "PUBLISHED-1", Operator, new PublishedReplayImageSource(f.Folder, Sample()));
        var archived = f.Store.Get(record.Id)!;
        Assert.Equal(InspectionExecution.Completed, archived.ExecutionStatus);
        Assert.Equal(QualityDecision.Fail, archived.Decision);
        Assert.Equal(f.Version.Bundle.VersionId.ToString("D"), archived.RecipeId);
        Assert.Equal(f.Loaded.RecipeJson, archived.RecipeJson);
        var version = JsonSerializer.Deserialize<PublishedRecipeVersion>(archived.RecipeJson, Json)!;
        Assert.Equal(f.Version.BundleHash, PublishedRecipeTransfer.Hash(version.Bundle));
        Assert.Equal(version.Bundle.References[0].Sha256, Hash(archived.ReferenceImage!));
        Assert.Equal(f.Reference, archived.ReferenceImage);
        Assert.Equal(f.Tested, archived.TestedImage);
        Assert.Equal(new double[] { 466, 276, 489, 302 }, Assert.Single(archived.Defects).Box);
        Assert.Equal(1, f.Store.PendingCount());
        Assert.False(coordinator.IsFaulted);
    }

    [Fact]
    public async Task ExplicitModeChangesPreserveEarlierVersionRecordsAndEngineeringReplay()
    {
        var f = Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        var engineering = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "ENGINEERING", Operator, new ReplayImageSource(f.Folder, Sample()));
        coordinator.UsePublishedRecipe(f.Loaded);
        var published = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "PUBLISHED", Operator, new PublishedReplayImageSource(f.Folder, Sample()));
        coordinator.UseDevelopmentRecipe(new ClassicalSettings(BoxPadding: 2));
        var changed = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "ENGINEERING-2", Operator, new ReplayImageSource(f.Folder, Sample()));
        Assert.StartsWith("classical-", engineering.RecipeId);
        Assert.Equal(new double[] { 460, 270, 495, 308 }, Assert.Single(engineering.Defects).Box);
        Assert.Equal(new double[] { 466, 276, 489, 302 }, Assert.Single(published.Defects).Box);
        Assert.Equal(new double[] { 468, 278, 487, 300 }, Assert.Single(changed.Defects).Box);
        Assert.Equal(f.Version.Bundle.VersionId.ToString("D"), f.Store.Get(published.Id)!.RecipeId);
        Assert.Equal(f.Loaded.RecipeJson, f.Store.Get(published.Id)!.RecipeJson);
        Assert.Equal(3, f.Store.PendingCount());
    }

    [Fact]
    public async Task BusyInspectionRejectsBothModeChangesAndKeepsItsSelectedVersion()
    {
        var f = Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        coordinator.UsePublishedRecipe(f.Loaded);
        var paused = new PausedSource(new PublishedReplayImageSource(f.Folder, Sample()));
        var inspection = coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "BUSY", Operator, paused);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Throws<InvalidOperationException>(() => coordinator.UseDevelopmentRecipe(new ClassicalSettings()));
            Assert.Throws<InvalidOperationException>(() => coordinator.UsePublishedRecipe(f.Loaded));
        }
        finally { paused.Release.SetResult(); }
        var record = await inspection;
        Assert.Equal(f.Loaded.VersionId.ToString("D"), record.RecipeId);
        Assert.Equal(f.Loaded.RecipeJson, record.RecipeJson);
        Assert.Equal(new double[] { 466, 276, 489, 302 }, Assert.Single(record.Defects).Box);
    }

    [Fact]
    public async Task FaultedStationCannotResumeBySwitchingRecipe()
    {
        var f = Setup();
        using (var connection = new SqliteConnection($"Data Source={f.Store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_outbox BEFORE INSERT ON UploadState BEGIN SELECT RAISE(ABORT, 'fixture disk failure'); END;";
            command.ExecuteNonQuery();
        }
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        coordinator.UsePublishedRecipe(f.Loaded);
        await Assert.ThrowsAsync<SqliteException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "FAULT", Operator, new PublishedReplayImageSource(f.Folder, Sample())));
        Assert.True(coordinator.IsFaulted);
        Assert.Throws<InvalidOperationException>(() => coordinator.UseDevelopmentRecipe(new ClassicalSettings()));
        Assert.Throws<InvalidOperationException>(() => coordinator.UsePublishedRecipe(f.Loaded));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "AFTER-FAULT", Operator, new PublishedReplayImageSource(f.Folder, Sample())));
        Assert.Single(f.Store.ReadRecent());
        Assert.Equal(0, f.Store.PendingCount());
    }

    [Fact]
    public async Task UnknownSampleIsArchivedAsFailedWithNoQualityPassOrDefaultRecipe()
    {
        var f = Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        coordinator.UsePublishedRecipe(f.Loaded);
        var record = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "UNKNOWN", Operator, new PublishedReplayImageSource(f.Folder, Sample("unknown")));
        Assert.Equal(InspectionExecution.Failed, record.ExecutionStatus);
        Assert.Equal(QualityDecision.NotEvaluated, record.Decision);
        Assert.Equal(f.Loaded.VersionId.ToString("D"), record.RecipeId);
        Assert.Equal(f.Tested, f.Store.Get(record.Id)!.TestedImage);
        Assert.Null(record.ReferenceImage);
        Assert.NotNull(record.Error);
        Assert.Empty(record.Defects);
    }

    [Fact]
    public async Task ConstructedNormalUsesCachedReferenceWithoutAnySourceFiles()
    {
        var f = Setup();
        File.Delete(Path.Combine(f.Folder, "reference.png"));
        File.Delete(Path.Combine(f.Folder, "tested.png"));
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        coordinator.UsePublishedRecipe(f.Loaded);
        var source = new PublishedReplayImageSource(f.Folder, Sample(), f.Loaded.GetReferenceBytes("controlled-1"));
        var record = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "CONSTRUCTED", Operator, source);
        Assert.Equal("ConstructedNormal", record.SourceKind);
        Assert.Equal(QualityDecision.Pass, record.Decision);
        Assert.Equal(f.Reference, record.TestedImage);
        Assert.Equal(f.Reference, record.ReferenceImage);
    }

    [Fact]
    public async Task TestedOnlySourceCannotSilentlyUseEngineeringDefaults()
    {
        var f = Setup();
        var coordinator = new InspectionCoordinator(f.Store, new ClassicalSettings());
        var record = await coordinator.InspectAsync(InspectionPurpose.EngineeringReplay, "ST-1", "WRONG-MODE", Operator, new PublishedReplayImageSource(f.Folder, Sample()));
        Assert.Equal(InspectionExecution.Failed, record.ExecutionStatus);
        Assert.Equal(QualityDecision.NotEvaluated, record.Decision);
        Assert.NotNull(record.Error);
        Assert.Equal(f.Tested, record.TestedImage);
    }

    [Fact]
    public void CacheListReturnsVersionMetadataAndLoadedRecipeExposesReadOnlySampleIds()
    {
        var f = Setup();
        var summary = Assert.Single(f.Recipes.List());
        Assert.Equal(f.Version.Bundle.VersionId, summary.Id);
        Assert.Equal(f.Version.BundleHash, summary.BundleHash);
        Assert.Equal(f.Version.Bundle.Name, summary.Name);
        Assert.Equal(f.Version.Bundle.Name, f.Loaded.Name);
        Assert.Equal(new[] { "controlled-1" }, f.Loaded.SampleIds);
    }

    internal sealed record Fixture(string Folder, LocalInspectionStore Store, LocalRecipeStore Recipes,
        LoadedRecipe Loaded, PublishedRecipeVersion Version, byte[] Reference, byte[] Tested);

    private sealed class PausedSource(IImageSource source) : IImageSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SampleId => source.SampleId;
        public string SourceKind => source.SourceKind;
        public async Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await source.CaptureAsync(cancellationToken);
        }
    }
}
