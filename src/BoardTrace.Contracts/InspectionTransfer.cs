using System.Security.Cryptography;
using System.Text.Json;

namespace BoardTrace.Contracts;

public sealed record InspectionReceipt(Guid InspectionId, string ContentHash, DateTimeOffset ReceivedAt);

public static class InspectionTransfer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Hash(InspectionRecord record)
    {
        var canonical = record with
        {
            Diagnostics = record.Diagnostics.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value)
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, Json)));
    }
}
