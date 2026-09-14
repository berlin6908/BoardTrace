using System.Net;

namespace BoardTrace.Station.Core;

public sealed class StationPersonnelSession : IDisposable
{
    public StationPersonnelSession(HttpClient client, CookieContainer cookies)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(cookies);
        Client = client;
        Cookies = cookies;
    }

    public HttpClient Client { get; }
    public CookieContainer Cookies { get; }
    public void Dispose() => Client.Dispose();
}
