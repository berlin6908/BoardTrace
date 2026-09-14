using System.Security.Cryptography;
using BoardTrace.Contracts;
using BoardTrace.Vision;
using OpenCvSharp;

namespace BoardTrace.Station.Core;

public sealed class LoadedRecipe : IDisposable
{
    private static readonly Lazy<string> AssemblyHash = new(() => Convert.ToHexStringLower(
        SHA256.HashData(File.ReadAllBytes(typeof(ClassicalDetector).Assembly.Location))));
    private readonly ClassicalDetector? classical;
    private readonly OnnxDetector? onnx;
    private readonly OnnxScoreThresholds? thresholds;
    private readonly IReadOnlyDictionary<string, byte[]> references;
    private bool disposed;

    public Guid VersionId { get; }
    public string BundleHash { get; }
    public string Name { get; }
    public IReadOnlyList<string> SampleIds { get; }
    public string RecipeJson { get; }
    public string Algorithm { get; }

    internal LoadedRecipe(PublishedRecipeVersion version, string recipeJson, IReadOnlyDictionary<Guid, byte[]> assets)
    {
        var bundle = version.Bundle;
        if (!string.Equals(AssemblyHash.Value, bundle.AlgorithmAssemblySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前检测程序集与发布方案不一致，不能加载此版本。");
        foreach (var assetId in bundle.References.Select(reference => reference.AssetId).Distinct())
        {
            try
            {
                using var image = Cv2.ImDecode(assets[assetId], ImreadModes.Grayscale);
                if (image.Empty() || image.Width != 640 || image.Height != 640)
                    throw new InvalidDataException("缓存参考图不能解码为 640×640 图像。");
            }
            catch (OpenCVException exception)
            {
                throw new InvalidDataException("缓存参考图无法解码。", exception);
            }
        }
        references = bundle.References.ToDictionary(reference => reference.SampleId, reference => assets[reference.AssetId], StringComparer.Ordinal);
        VersionId = bundle.VersionId;
        BundleHash = version.BundleHash;
        Name = bundle.Name;
        SampleIds = Array.AsReadOnly(references.Keys.Order(StringComparer.Ordinal).ToArray());
        RecipeJson = recipeJson;
        Algorithm = bundle.Definition.Algorithm;
        switch (bundle.Definition)
        {
            case ClassicalRecipeDefinition(var settings):
                classical = new ClassicalDetector(new ClassicalSettings(settings.BinarizationThreshold, settings.EdgeTolerance,
                    settings.MinimumArea, settings.ClosingSize, settings.BoxPadding, settings.MaximumTranslation, settings.MinimumAlignmentResponse));
                break;
            case PairedOnnxRecipeDefinition(var sha, var scores):
                thresholds = OnnxScoreThresholds.FromClassOrder([scores.Open, scores.Short, scores.Mousebite, scores.Spur, scores.Copper, scores.PinHole]);
                onnx = new OnnxDetector(assets[bundle.Model!.AssetId], sha);
                break;
            default:
                throw new InvalidDataException("不支持该检测方案类型。");
        }
    }

    public DetectionResult Detect(string sampleId, byte[] testedBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!references.TryGetValue(sampleId, out var reference))
            throw new InvalidDataException("该样本不在已发布方案的参考图清单中。");
        return onnx is not null ? onnx.Detect(testedBytes, reference, thresholds!, cancellationToken)
            : classical!.Detect(testedBytes, reference, cancellationToken);
    }

    public byte[] GetReferenceBytes(string sampleId)
    {
        ThrowIfDisposed();
        if (!references.TryGetValue(sampleId, out var reference))
            throw new InvalidDataException("该样本不在已发布方案的参考图清单中。");
        return reference.ToArray();
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    // The coordinator serializes detection and disposal after accepting ownership.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        onnx?.Dispose();
    }
}
