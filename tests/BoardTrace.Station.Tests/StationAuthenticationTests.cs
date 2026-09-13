using System.Net;
using System.Net.Http.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;

namespace BoardTrace.Station.Tests;

public sealed class StationAuthenticationTests
{
    [Theory]
    [InlineData("QualityEngineer")]
    [InlineData("ProcessEngineer")]
    [InlineData("Station")]
    public async Task SuccessfulLoginWithANonOperatorRoleCannotEnterTheStation(string role)
    {
        using var client = new HttpClient(new LoginHandler(role)) { BaseAddress = new Uri("http://localhost/") };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => StationAuthentication.LoginOperatorAsync(client,
            new LoginRequest("test-person", "test-password")));
    }

    private sealed class LoginHandler(string role) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CurrentUser("person-1", "test-person", "Test Person", [role], null))
            });
        }
    }
}
