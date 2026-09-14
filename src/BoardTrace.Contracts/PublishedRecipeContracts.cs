using System.Security.Cryptography;
using System.Text.Json;

namespace BoardTrace.Contracts;

public sealed record PublishRecipeRequest(Guid ValidationRunId);

public sealed record RecipeReleasePolicy(bool IsFrozen, RecipeTargets? Targets, string? Reason);

public sealed record PublishedRecipeInput(int Width, int Height, bool RequiresReference);

public sealed record PublishedRecipeReference(string SampleId, Guid AssetId, string Sha256, int ByteLength);

public sealed record PublishedRecipeModel(Guid AssetId, string Sha256, int ByteLength, string InputContract);

public sealed record PublishedRecipeBundle(Guid VersionId, Guid DraftId, Guid ValidationRunId, string Name,
    RecipeDefinition Definition, RecipeTargets Targets, RecipeTargets ReleaseTargets, PublishedRecipeInput Input,
    string AlgorithmAssemblySha256, string InputManifestSha256, string ValidationSnapshotHash,
    IReadOnlyList<PublishedRecipeReference> References, PublishedRecipeModel? Model, string PublishedById, string PublishedByName,
    DateTimeOffset PublishedAt);

public sealed record PublishedRecipeVersion(PublishedRecipeBundle Bundle, string BundleHash);

public sealed record PublishedRecipeSummary(Guid Id, Guid DraftId, Guid ValidationRunId, string Name,
    string Algorithm, string BundleHash, string PublishedById, string PublishedByName, DateTimeOffset PublishedAt);

public static class PublishedRecipeTransfer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Hash only the bundle, using its persisted identities/timestamp and ordinal sample order.
    public static string Hash(PublishedRecipeBundle bundle) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(bundle with
        {
            References = bundle.References.OrderBy(reference => reference.SampleId, StringComparer.Ordinal).ToArray()
        }, Json)));
}
