using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Recipes;

namespace BoardTrace.Server.Tests;

public sealed class RecipeScoringTests
{
    [Fact]
    public void ClassAwareMatchingPreservesLowScoresStableTiesAndIouBoundary()
    {
        RecipeTruth[] truth = [new([0, 0, 10, 10], 1), new([0, 0, 10, 10], 2), new([30, 0, 40, 10], 3)];
        RecipePrediction[] predictions = [new([0, 0, 10, 10], 1, .17, 100), new([0, 0, 10, 10], 1, .17, 100),
            new([30, 0, 40, 10], 4, .9, 100), new([0, 0, 5, 10], 2, .22, 50)];
        var classified = RecipeScoring.Match(predictions, truth, true);
        Assert.Equal((2, 2, 1), (classified.Tp, classified.Fp, classified.Fn));
        Assert.Equal(1, classified.Classes[0].Fp);
        Assert.Equal(1, classified.Classes[2].Fn);
        Assert.Equal(1, classified.Classes[3].Fp);
        var localization = RecipeScoring.Match(predictions, truth, false);
        Assert.Equal((3, 1, 0), (localization.Tp, localization.Fp, localization.Fn));
        Assert.Empty(localization.Classes);
        var failed = RecipeScoring.Match([], truth, true);
        Assert.Equal(3, failed.Fn);
        Assert.Equal(new[] { 1, 1, 1, 0, 0, 0 }, failed.Classes.Select(x => x.Fn));
    }

    [Fact]
    public void DefinitionRoundTripsAndCannotSilentlyDefaultMissingClassThreshold()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var draft = new SaveRecipeDraft("paired", new PairedOnnxRecipeDefinition(new string('a', 64), new(.17, .22, .90, .68, .43, .59)), new(.9, .95, 1500));
        var encoded = JsonSerializer.Serialize(draft, options);
        var decoded = JsonSerializer.Deserialize<SaveRecipeDraft>(encoded, options)!;
        Assert.Equal(draft, decoded);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SaveRecipeDraft>(encoded.Replace(",\"pinHole\":0.59", ""), options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SaveRecipeDraft>(encoded.Replace("PairedOnnx", "Unknown"), options));
    }
}
