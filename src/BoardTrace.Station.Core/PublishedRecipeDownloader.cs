using System.Net.Http.Json;
using BoardTrace.Contracts;

namespace BoardTrace.Station.Core;

public sealed class PublishedRecipeDownloader(LocalRecipeStore store, HttpClient client)
{
    public async Task<LoadedRecipe> DownloadAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await client.GetFromJsonAsync<PublishedRecipeVersion>($"api/recipes/versions/{versionId:D}/bundle", cancellationToken)
            ?? throw new InvalidDataException("中央没有返回已发布方案包。");
        LocalRecipeStore.ValidateBundle(version);
        if (version.Bundle.VersionId != versionId)
            throw new InvalidDataException("中央返回的方案身份与请求不符。");
        var assets = new Dictionary<Guid, byte[]>();
        foreach (var assetId in LocalRecipeStore.AssetIds(version.Bundle))
            assets.Add(assetId, await client.GetByteArrayAsync($"api/recipe-assets/{assetId:D}", cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() =>
        {
            store.Save(version, assets);
            return store.Load(versionId);
        }, cancellationToken);
    }
}
