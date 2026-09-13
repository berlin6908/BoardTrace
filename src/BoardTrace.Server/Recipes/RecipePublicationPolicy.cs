using System.Text.Json;
using BoardTrace.Contracts;

namespace BoardTrace.Server.Recipes;

public static class RecipePublicationPolicy
{
    public static async Task<RecipeReleasePolicy> ReadAsync(IConfiguration config, CancellationToken token)
    {
        var path = config["RecipePublication:ReleasePolicyPath"];
        if (string.IsNullOrWhiteSpace(path)) return new(false, null, "服务端独立发布策略未配置，不能发布。");
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid();
            if (!root.TryGetProperty("qualityTargetsFrozen", out var frozen) || frozen.ValueKind != JsonValueKind.True)
                return new(false, null, "独立质量目标尚未冻结，不能发布。");
            if (!root.TryGetProperty("qualityTargets", out var targets) || targets.ValueKind != JsonValueKind.Object ||
                !Number(targets, "minPrecision", out var precision) || !Number(targets, "minRecall", out var recall) ||
                !Number(targets, "maxP95Ms", out var p95) || precision is < 0 or > 1 || recall is < 0 or > 1 || p95 <= 0)
                return Invalid();
            return new(true, new RecipeTargets(precision, recall, p95), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, null, "独立发布策略文件不可用，不能发布。");
        }
        catch (JsonException) { return Invalid(); }
    }

    private static bool Number(JsonElement targets, string name, out double value)
    {
        value = 0;
        return targets.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static RecipeReleasePolicy Invalid() => new(false, null, "独立发布策略目标格式无效，不能发布。");
}
