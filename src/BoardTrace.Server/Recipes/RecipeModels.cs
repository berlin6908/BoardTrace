using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardTrace.Contracts;

namespace BoardTrace.Server.Recipes;

public sealed class RecipeDraft
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string DefinitionJson { get; set; }
    public required string TargetsJson { get; set; }
    public required string DataManifestSha256 { get; set; }
    public required string SnapshotHash { get; set; }
    public required string AuthorId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public RecipeDraftView View() => new(Id, Name,
        JsonSerializer.Deserialize<RecipeDefinition>(DefinitionJson)!,
        JsonSerializer.Deserialize<RecipeTargets>(TargetsJson)!, DataManifestSha256, SnapshotHash, UpdatedAt);

    public static string Hash(string name, string settingsJson, string targetsJson, string manifestHash) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { name, settingsJson, targetsJson, manifestHash }))));
}

public sealed class ValidationRun
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public required string Status { get; set; }
    public required string Name { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
    public required string SnapshotHash { get; set; }
    public required string DefinitionJson { get; set; }
    public required string TargetsJson { get; set; }
    public required string ManifestHash { get; set; }
    public required string TruthHash { get; set; }
    public string? ReportJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public RecipeValidationView View() => new(Id, DraftId, Status, Processed, Total,
        new RecipeValidationSnapshot(Name, JsonSerializer.Deserialize<RecipeDefinition>(DefinitionJson)!,
            JsonSerializer.Deserialize<RecipeTargets>(TargetsJson)!, ManifestHash, SnapshotHash),
        ReportJson is null ? null : JsonSerializer.Deserialize<RecipeValidationReport>(ReportJson),
        Error, CreatedAt, CompletedAt);
}

public sealed class RecipeModel
{
    public required string Sha256 { get; set; }
    public int ByteLength { get; set; }
    public required string InputContract { get; set; }
    public required byte[] Content { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public RecipeModelSummary View() => new(Sha256, ByteLength, InputContract, CreatedAt);
}
