using System.Text.Json;
using BoardTrace.Contracts;

namespace BoardTrace.Server.Recipes;

public sealed class RecipePublication
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Guid ValidationRunId { get; set; }
    public required string Name { get; set; }
    public required string BundleJson { get; set; }
    public required string BundleHash { get; set; }
    public required string PublishedById { get; set; }
    public required string PublishedByName { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public List<RecipeReferenceAsset> Assets { get; set; } = [];

    public PublishedRecipeVersion View() => new(JsonSerializer.Deserialize<PublishedRecipeBundle>(BundleJson, Json)!, BundleHash);
}

public sealed class RecipeReferenceAsset
{
    public Guid Id { get; set; }
    public Guid RecipeVersionId { get; set; }
    public required string Sha256 { get; set; }
    public required byte[] Content { get; set; }
}
