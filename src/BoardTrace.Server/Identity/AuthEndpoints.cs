using System.Security.Claims;
using BoardTrace.Contracts;
using Microsoft.AspNetCore.Identity;

namespace BoardTrace.Server.Identity;

public static class AuthEndpoints
{
    public const string HumanRoles = "Operator,ProcessEngineer,QualityEngineer";
    public const string StationRole = "Station";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapGet("/me", async (ClaimsPrincipal principal, UserManager<BoardTraceUser> users, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private,no-store";
            var user = await users.GetUserAsync(principal);
            return user is null ? Results.Unauthorized() : Results.Ok(await ToCurrentUser(user, users));
        }).RequireAuthorization();
        auth.MapPost("/login", async (LoginRequest request, UserManager<BoardTraceUser> users,
            SignInManager<BoardTraceUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
                return Results.Problem(statusCode: 401, title: "用户名或密码错误。");
            var user = await users.FindByNameAsync(request.UserName);
            if (user is null || !(await signIn.PasswordSignInAsync(user, request.Password, false, lockoutOnFailure: false)).Succeeded)
                return Results.Problem(statusCode: 401, title: "用户名或密码错误。");
            return Results.Ok(await ToCurrentUser(user, users));
        });
        auth.MapPost("/logout", async (SignInManager<BoardTraceUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task<CurrentUser> ToCurrentUser(BoardTraceUser user, UserManager<BoardTraceUser> users) =>
        new(user.Id, user.UserName!, user.DisplayName, (await users.GetRolesAsync(user)).ToArray(), user.StationId);
}
