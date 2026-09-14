using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using BoardTrace.Contracts;
using BoardTrace.Station;
using BoardTrace.Station.Core;
using BoardTrace.Vision;

namespace BoardTrace.Station.Smoke;

public static partial class Program
{
    // An isolated UI/cache fixture, never a central publication or a quality-approved recipe.
    private static async Task VerifyPublishedRecipeAsync(string output, StationOptions defaults)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var firstLine = File.ReadLines(defaults.ManifestPath).First(line => !string.IsNullOrWhiteSpace(line));
        var source = JsonSerializer.Deserialize<ReplaySample>(firstLine, json)!;
        var reference = await File.ReadAllBytesAsync(Path.Combine(defaults.DataRoot, source.Reference));
        var tested = await File.ReadAllBytesAsync(Path.Combine(defaults.DataRoot, source.Image));
        var fixtureRoot = Path.Combine(output, "recipe-input");
        Directory.CreateDirectory(fixtureRoot);
        await File.WriteAllBytesAsync(Path.Combine(fixtureRoot, "tested.jpg"), tested);
        var sample = new ReplaySample(source.SampleId, "tested.jpg", "reference-does-not-exist.jpg");
        var manifest = Path.Combine(fixtureRoot, "inputs.jsonl");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(sample, json));
        var options = defaults with { DataRoot = fixtureRoot, ManifestPath = manifest, DatabasePath = Path.Combine(output, "recipe-ui.db") };
        var assets = new Dictionary<Guid, byte[]> { [Guid.NewGuid()] = reference };
        var settings = new RecipeClassicalSettings(127, 1, 8, 3, 4, 12, 0.1);
        var targets = new RecipeTargets(0.99, 0.99, 100);
        var bundle = new PublishedRecipeBundle(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "UI 缓存夹具 · 非质量发布",
            new ClassicalRecipeDefinition(settings), targets, targets, new(640, 640, true),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(ClassicalDetector).Assembly.Location))),
            new string('1', 64), new string('2', 64),
            [new(sample.SampleId, assets.Keys.Single(), Convert.ToHexStringLower(SHA256.HashData(reference)), reference.Length)],
            null, "ui-fixture", "隔离 UI 夹具", DateTimeOffset.UtcNow);
        var version = new PublishedRecipeVersion(bundle, PublishedRecipeTransfer.Hash(bundle));
        var cache = new LocalRecipeStore(options.DatabasePath);
        cache.Initialize();
        cache.Save(version, assets);
        using var personnel = PersonnelSession(new RecipeUiHandler());
        await using var model = new StationViewModel(options, OfflineOperator, personnel, false);
        var window = new MainWindow { DataContext = model };
        window.Show();
        await model.InitializeAsync();
        var expander = (Expander)window.FindName("RecipeExpander");
        expander.IsExpanded = true;
        Require(model.PublishedRecipes.Single().Cached && model.LoadCachedRecipeCommand.CanExecute(null),
            "The complete cached recipe was not offered after station initialization.");
        await model.LoadCachedRecipeCommand.ExecuteAsync(null);
        Require(model.ActiveRecipeIdentity == bundle.VersionId.ToString() && model.Samples.Count == 1,
            "Loading the selected cached version did not activate its identity and sample mapping.");
        model.ProductId = "SIM-CACHED-RECIPE-DEFECT";
        await model.RunCommand.ExecuteAsync(null);
        var inspectionId = Guid.Parse(model.InspectionId);
        var store = new LocalInspectionStore(options.DatabasePath);
        var record = store.Get(inspectionId)!;
        var expected = new ClassicalDetector(new ClassicalSettings(127, 1, 8, 3, 4, 12, 0.1)).Detect(tested, reference);
        Require(record.ExecutionStatus == InspectionExecution.Completed && record.RecipeId == bundle.VersionId.ToString()
            && record.RecipeJson == cache.Load(bundle.VersionId).RecipeJson
            && record.ReferenceImage!.SequenceEqual(reference) && record.TestedImage!.SequenceEqual(tested)
            && JsonSerializer.Serialize(record.Defects.Select(defect => defect.Box)) == JsonSerializer.Serialize(expected.Defects.Select(defect => defect.Box)),
            "Actual WPF replay did not persist the selected version, non-default settings and cached reference evidence.");
        await SnapshotAsync(window, Path.Combine(output, "08-cached-recipe-fixture.png"));
        model.ConstructedNormal = true;
        model.ProductId = "SIM-CACHED-RECIPE-CONSTRUCTED-NORMAL";
        await model.RunCommand.ExecuteAsync(null);
        var normal = store.Get(Guid.Parse(model.InspectionId))!;
        Require(normal.Decision == QualityDecision.Pass && normal.SourceKind == "ConstructedNormal"
            && normal.ReferenceImage!.SequenceEqual(reference) && normal.TestedImage!.SequenceEqual(reference),
            "Constructed normal replay did not use the cached published reference image.");
        await model.RefreshRecipesCommand.ExecuteAsync(null);
        Require(model.RecipeNotice.StartsWith("刷新版本失败") && model.ActiveRecipeIdentity == bundle.VersionId.ToString()
            && model.RunCommand.CanExecute(null), "A failed version refresh silently changed the active recipe or disabled recovery.");
        await SnapshotAsync(window, Path.Combine(output, "09-recipe-refresh-recovery.png"));
        model.UseDevelopmentRecipeCommand.Execute(null);
        Require(model.ActiveRecipeName == "经典图像差分 · 开发参数" && !model.UseDevelopmentRecipeCommand.CanExecute(null),
            "Explicit development mode selection did not release the published recipe selection.");
        await File.WriteAllTextAsync(Path.Combine(output, "published-recipe-ui-fixture.json"), JsonSerializer.Serialize(new
        {
            completedAt = DateTimeOffset.UtcNow, scope = "Isolated UI/cache fixture; not a central publication or a quality approval",
            versionId = bundle.VersionId, inspectionId, normalId = normal.Id,
            missingSourceReference = !File.Exists(Path.Combine(fixtureRoot, sample.Reference)),
            persistedBoxPadding = settings.BoxPadding,
            checks = new[] { "cached versions listed after startup", "explicit cached version selection", "real WPF detection uses cached reference and non-default parameters",
                "complete immutable recipe in local archive", "constructed normal uses cached reference", "failed refresh retains active version", "explicit development mode" }
        }, new JsonSerializerOptions(json) { WriteIndented = true }));
        window.Close();
    }

    private static T? FindVisual<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        if (root is T found) return found;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }

    private sealed class RecipeUiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/me" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(OfflineOperator) },
                "/api/auth/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
                "/api/recipes/versions" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException("Unexpected UI fixture request.")
            });
    }
}
