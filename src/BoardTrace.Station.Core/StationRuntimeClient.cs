using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;

namespace BoardTrace.Station.Core;

public sealed class StationRuntimeClient(HttpClient client, StationCredentials credentials, string stationId)
{
    public async Task<StationRuntimeReceipt> ReportAsync(StationRuntimeUpdate report, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(report, cancellationToken);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<StationRuntimeReceipt>(cancellationToken)
            ?? throw new InvalidDataException("中央未返回工位上报回执。");
        if (receipt.StationId != stationId)
            throw new InvalidDataException("中央上报回执未对应当前工位。");
        return receipt;
    }

    private async Task<HttpResponseMessage> SendAsync(StationRuntimeUpdate report, CancellationToken token)
    {
        var path = $"api/stations/{Uri.EscapeDataString(stationId)}/runtime";
        var response = await client.PutAsJsonAsync(path, report, token);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        await StationAuthentication.LoginDeviceAsync(client, credentials, stationId, token);
        return await client.PutAsJsonAsync(path, report, token);
    }
}
