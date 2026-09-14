using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;

namespace BoardTrace.Station.Core;

public static class StationAuthentication
{
    public static HttpClient CreateClient(Uri server) => CreateClient(server, new CookieContainer());

    public static StationPersonnelSession CreatePersonnelSession(Uri server)
    {
        var cookies = new CookieContainer();
        return new StationPersonnelSession(CreateClient(server, cookies), cookies);
    }

    private static HttpClient CreateClient(Uri server, CookieContainer cookies) => new(new HttpClientHandler
    {
        CookieContainer = cookies, AllowAutoRedirect = false
    }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(10) };

    public static CurrentUser RequireOperator(CurrentUser? user)
    {
        if (user is null || !user.Roles.Contains("Operator", StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(user.Id) || string.IsNullOrWhiteSpace(user.DisplayName))
            throw new UnauthorizedAccessException("请使用具备操作员权限的人员账号登录工位。");
        return user;
    }

    public static async Task<CurrentUser> LoginOperatorAsync(HttpClient client, LoginRequest login, CancellationToken token = default) =>
        RequireOperator(await LoginAsync(client, login, token));

    public static async Task<CurrentUser> CurrentOperatorAsync(HttpClient client, CancellationToken token = default)
    {
        using var response = await client.GetAsync("api/auth/me", token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("人员登录已失效，请重新登录。");
        response.EnsureSuccessStatusCode();
        return RequireOperator(await response.Content.ReadFromJsonAsync<CurrentUser>(token));
    }

    public static async Task LoginDeviceAsync(HttpClient client, StationCredentials credentials, string stationId, CancellationToken token)
    {
        var user = await LoginAsync(client, new LoginRequest(credentials.UserName, credentials.Password), token);
        if (!user.Roles.Contains("Station", StringComparer.Ordinal) || user.StationId != stationId)
            throw new UnauthorizedAccessException("设备账号未绑定当前工位，待上传原件保持不变。");
    }

    private static async Task<CurrentUser> LoginAsync(HttpClient client, LoginRequest login, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("api/auth/login", login, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("账号或密码无效，或账号暂时被锁定。");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CurrentUser>(token)
            ?? throw new UnauthorizedAccessException("中央未返回有效登录身份。");
    }
}
