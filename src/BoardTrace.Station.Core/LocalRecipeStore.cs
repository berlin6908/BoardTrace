using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using Microsoft.Data.Sqlite;

namespace BoardTrace.Station.Core;

public sealed class LocalRecipeStore(string databasePath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string DatabasePath { get; } = Path.GetFullPath(databasePath);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Recipes (
                VersionId TEXT PRIMARY KEY,
                BundleHash TEXT NOT NULL,
                Document TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS RecipeAssets (
                VersionId TEXT NOT NULL REFERENCES Recipes(VersionId),
                AssetId TEXT NOT NULL,
                Content BLOB NOT NULL,
                PRIMARY KEY (VersionId, AssetId)
            );
            """;
        command.ExecuteNonQuery();
    }

    // The caller downloads all assets first. Copy and verify before opening the write transaction.
    public void Save(PublishedRecipeVersion version, IReadOnlyDictionary<Guid, byte[]> assets)
    {
        var document = JsonSerializer.Serialize(version, Json);
        var snapshot = ReadVersion(document);
        var copiedAssets = assets.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        ValidateContents(snapshot, copiedAssets);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT BundleHash FROM Recipes WHERE VersionId=$id;";
        existing.Parameters.AddWithValue("$id", snapshot.Bundle.VersionId.ToString());
        if (existing.ExecuteScalar() is string existingHash)
        {
            if (!string.Equals(existingHash, snapshot.BundleHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("同一方案版本不能替换为不同内容。");
            return;
        }

        using var recipe = connection.CreateCommand();
        recipe.Transaction = transaction;
        recipe.CommandText = "INSERT INTO Recipes(VersionId,BundleHash,Document) VALUES ($id,$hash,$document);";
        recipe.Parameters.AddWithValue("$id", snapshot.Bundle.VersionId.ToString());
        recipe.Parameters.AddWithValue("$hash", snapshot.BundleHash);
        recipe.Parameters.AddWithValue("$document", document);
        recipe.ExecuteNonQuery();
        foreach (var (id, bytes) in copiedAssets)
        {
            using var asset = connection.CreateCommand();
            asset.Transaction = transaction;
            asset.CommandText = "INSERT INTO RecipeAssets(VersionId,AssetId,Content) VALUES ($version,$asset,$content);";
            asset.Parameters.AddWithValue("$version", snapshot.Bundle.VersionId.ToString());
            asset.Parameters.AddWithValue("$asset", id.ToString());
            asset.Parameters.Add("$content", SqliteType.Blob).Value = bytes;
            asset.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public LoadedRecipe Load(Guid versionId)
    {
        string document;
        string storedHash;
        var assets = new Dictionary<Guid, byte[]>();
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            using var recipe = connection.CreateCommand();
            recipe.Transaction = transaction;
            recipe.CommandText = "SELECT Document,BundleHash FROM Recipes WHERE VersionId=$id;";
            recipe.Parameters.AddWithValue("$id", versionId.ToString());
            using (var reader = recipe.ExecuteReader())
            {
                if (!reader.Read()) throw new KeyNotFoundException("本地尚未缓存指定方案版本。");
                document = reader.GetString(0);
                storedHash = reader.GetString(1);
            }
            using var readAssets = connection.CreateCommand();
            readAssets.Transaction = transaction;
            readAssets.CommandText = "SELECT AssetId,Content FROM RecipeAssets WHERE VersionId=$id;";
            readAssets.Parameters.AddWithValue("$id", versionId.ToString());
            using (var reader = readAssets.ExecuteReader())
                while (reader.Read()) assets.Add(Guid.Parse(reader.GetString(0)), (byte[])reader[1]);
            transaction.Commit();
        }
        var version = ReadVersion(document);
        if (version.Bundle.VersionId != versionId || !string.Equals(storedHash, version.BundleHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本地方案版本身份或哈希已损坏。");
        ValidateContents(version, assets);
        return new LoadedRecipe(version, document, assets);
    }

    // Listing reads package metadata and asset counts only. Load performs full BLOB verification before use.
    public IReadOnlyList<PublishedRecipeSummary> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.VersionId,r.Document,r.BundleHash,
                (SELECT COUNT(*) FROM RecipeAssets a WHERE a.VersionId=r.VersionId)
            FROM Recipes r;
            """;
        using var reader = command.ExecuteReader();
        var summaries = new List<PublishedRecipeSummary>();
        while (reader.Read())
        {
            var version = ReadVersion(reader.GetString(1));
            ValidateBundle(version);
            var bundle = version.Bundle;
            if (bundle.VersionId.ToString() != reader.GetString(0)
                || !string.Equals(version.BundleHash, reader.GetString(2), StringComparison.OrdinalIgnoreCase)
                || AssetIds(bundle).Count() != reader.GetInt64(3))
                throw new InvalidDataException("本地缓存方案身份或资产集合不完整。");
            summaries.Add(new PublishedRecipeSummary(bundle.VersionId, bundle.DraftId, bundle.ValidationRunId,
                bundle.Name, bundle.Definition.Algorithm, version.BundleHash, bundle.PublishedById, bundle.PublishedByName, bundle.PublishedAt));
        }
        return summaries.OrderByDescending(summary => summary.PublishedAt).ThenBy(summary => summary.Id).ToArray();
    }

    private static PublishedRecipeVersion ReadVersion(string document)
    {
        try
        {
            var version = JsonSerializer.Deserialize<PublishedRecipeVersion>(document, Json);
            if (version?.Bundle is null) throw new InvalidDataException("方案包缺少版本内容。");
            return version;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("方案包 JSON 无效。", exception);
        }
    }

    internal static void ValidateContents(PublishedRecipeVersion version, IReadOnlyDictionary<Guid, byte[]> assets)
    {
        ValidateBundle(version);
        var bundle = version.Bundle;
        if (assets.Count != AssetIds(bundle).Count())
            throw new InvalidDataException("方案资产集合不完整。");
        foreach (var reference in bundle.References)
        {
            if (!assets.TryGetValue(reference.AssetId, out var bytes) || bytes.Length != reference.ByteLength
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"参考图 {reference.SampleId} 缺失，或长度/SHA-256 与发布版本不一致。");
        }
        if (bundle.Model is { } model && (!assets.TryGetValue(model.AssetId, out var modelBytes)
            || modelBytes.Length != model.ByteLength
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(modelBytes)), model.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("模型缺失，或长度/SHA-256 与发布版本不一致。");
    }

    internal static IEnumerable<Guid> AssetIds(PublishedRecipeBundle bundle) => bundle.References.Select(reference => reference.AssetId)
        .Concat(bundle.Model is { } model ? [model.AssetId] : Array.Empty<Guid>()).Distinct();

    internal static void ValidateBundle(PublishedRecipeVersion version)
    {
        var bundle = version.Bundle;
        if (bundle is null || bundle.References is null || bundle.References.Count == 0
            || bundle.Input is not { Width: 640, Height: 640, RequiresReference: true })
            throw new InvalidDataException("当前工位需要包含参考图的 640×640 方案。");
        if (bundle.References.Any(reference => reference is null || string.IsNullOrWhiteSpace(reference.SampleId))
            || bundle.References.Select(reference => reference.SampleId).Distinct(StringComparer.Ordinal).Count() != bundle.References.Count)
            throw new InvalidDataException("方案参考图样本标识缺失或重复。");
        switch (bundle.Definition)
        {
            case ClassicalRecipeDefinition { Settings: not null } when bundle.Model is null:
                break;
            case PairedOnnxRecipeDefinition { Thresholds: not null } paired when bundle.Model is { ByteLength: > 0 } model
                && model.InputContract == RecipeModelInput.PairedGrayAbsDiff640V1
                && string.Equals(paired.ModelSha256, model.Sha256, StringComparison.OrdinalIgnoreCase)
                && !bundle.References.Any(reference => reference.AssetId == model.AssetId):
                var scores = paired.Thresholds;
                if (new[] { scores.Open, scores.Short, scores.Mousebite, scores.Spur, scores.Copper, scores.PinHole }
                    .Any(score => !double.IsFinite(score) || score is < 0 or > 1))
                    throw new InvalidDataException("模型的六类阈值必须在 0 到 1 之间。");
                break;
            default:
                throw new InvalidDataException("方案类型与模型资产或输入契约不一致。");
        }
        if (!string.Equals(PublishedRecipeTransfer.Hash(bundle), version.BundleHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("方案包哈希与发布版本不一致。");
    }
}
