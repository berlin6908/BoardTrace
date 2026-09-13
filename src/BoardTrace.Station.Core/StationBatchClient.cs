using BoardTrace.Contracts;
using System.Net.Http.Json;

namespace BoardTrace.Station.Core;

public sealed class StationBatchClient(HttpClient client, StationCredentials credentials, string stationId)
{
    public async Task<BatchSummary[]> GetAssignedAsync(CancellationToken cancellationToken = default)
    {
        await StationAuthentication.LoginDeviceAsync(client, credentials, stationId, cancellationToken);
        var batches = await client.GetFromJsonAsync<BatchSummary[]>("api/station/batches", cancellationToken)
            ?? throw new InvalidDataException("中央没有返回工位批次列表。");
        if (batches.Any(summary => summary.Batch is null || summary.Batch.StationId != stationId))
            throw new InvalidDataException("中央批次列表包含其他工位的分配。");
        return batches;
    }

    public async Task<(BatchPackage Package, LoadedClassicalRecipe Recipe)> DownloadAsync(Guid batchId, LocalRecipeStore store,
        CancellationToken cancellationToken = default)
    {
        await StationAuthentication.LoginDeviceAsync(client, credentials, stationId, cancellationToken);
        var package = await client.GetFromJsonAsync<BatchPackage>($"api/station/batches/{batchId:D}/package", cancellationToken)
            ?? throw new InvalidDataException("中央没有返回批次方案包。");
        if (package.Batch is null || package.Batch.Id != batchId || package.Batch.StationId != stationId)
            throw new InvalidDataException("中央返回的批次或工位与请求不符。");
        if (package.Recipe?.Bundle is null || package.Batch.RecipeVersionId != package.Recipe.Bundle.VersionId ||
            package.Batch.RecipeBundleHash != package.Recipe.BundleHash)
            throw new InvalidDataException("批次固定版本或方案包哈希不一致。");
        LocalRecipeStore.ValidateBundle(package.Recipe);
        var loaded = await new PublishedRecipeDownloader(store, client).DownloadAsync(package.Batch.RecipeVersionId, cancellationToken);
        if (loaded.VersionId != package.Batch.RecipeVersionId || loaded.BundleHash != package.Batch.RecipeBundleHash)
            throw new InvalidDataException("已下载方案与批次固定版本不一致。");
        return (package, loaded);
    }
}
