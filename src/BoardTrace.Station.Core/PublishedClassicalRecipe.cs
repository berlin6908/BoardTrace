using System.Security.Cryptography;
using BoardTrace.Contracts;
using BoardTrace.Vision;
using OpenCvSharp;

namespace BoardTrace.Station.Core;

public sealed class LoadedClassicalRecipe
{
    private static readonly Lazy<string> AssemblyHash = new(() => Convert.ToHexStringLower(
        SHA256.HashData(File.ReadAllBytes(typeof(ClassicalDetector).Assembly.Location))));
    private readonly ClassicalDetector detector;
    private readonly IReadOnlyDictionary<string, byte[]> references;

    public Guid VersionId { get; }
    public string Name { get; }
    public IReadOnlyList<string> SampleIds { get; }
    public string RecipeJson { get; }

    internal LoadedClassicalRecipe(PublishedRecipeVersion version, string recipeJson, IReadOnlyDictionary<Guid, byte[]> assets)
    {
        var bundle = version.Bundle;
        if (!string.Equals(AssemblyHash.Value, bundle.AlgorithmAssemblySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("当前经典检测程序集与发布方案不一致，不能加载此版本。");
        foreach (var bytes in assets.Values)
        {
            try
            {
                using var image = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
                if (image.Empty() || image.Width != 640 || image.Height != 640)
                    throw new InvalidDataException("缓存参考图不能解码为 640×640 图像。");
            }
            catch (OpenCVException exception)
            {
                throw new InvalidDataException("缓存参考图无法解码。", exception);
            }
        }
        references = bundle.References.ToDictionary(reference => reference.SampleId, reference => assets[reference.AssetId], StringComparer.Ordinal);
        var settings = bundle.Settings;
        detector = new ClassicalDetector(new ClassicalSettings(settings.BinarizationThreshold, settings.EdgeTolerance,
            settings.MinimumArea, settings.ClosingSize, settings.BoxPadding, settings.MaximumTranslation, settings.MinimumAlignmentResponse));
        VersionId = bundle.VersionId;
        Name = bundle.Name;
        SampleIds = Array.AsReadOnly(references.Keys.Order(StringComparer.Ordinal).ToArray());
        RecipeJson = recipeJson;
    }

    public DetectionResult Detect(string sampleId, byte[] testedBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!references.TryGetValue(sampleId, out var reference))
            throw new InvalidDataException("该样本不在已发布方案的参考图清单中。");
        return detector.Detect(testedBytes, reference, cancellationToken);
    }

    public byte[] GetReferenceBytes(string sampleId)
    {
        if (!references.TryGetValue(sampleId, out var reference))
            throw new InvalidDataException("该样本不在已发布方案的参考图清单中。");
        return reference.ToArray();
    }
}
