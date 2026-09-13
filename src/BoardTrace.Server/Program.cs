using BoardTrace.Server.Storage;
using BoardTrace.Server.Inspections;
using Microsoft.EntityFrameworkCore;
using BoardTrace.Server.Identity;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddDbContext<BoardTraceDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("BoardTrace") ?? throw new InvalidOperationException("ConnectionStrings:BoardTrace is required."),
    sql => sql.UseCompatibilityLevel(150)));
builder.Services.AddIdentity<BoardTraceUser, IdentityRole>()
    .AddEntityFrameworkStores<BoardTraceDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Human", policy => policy.RequireRole("Operator", "ProcessEngineer", "QualityEngineer"))
    .AddPolicy("Station", policy => policy.RequireRole("Station"));
var app = builder.Build();
app.UseExceptionHandler();
if (args.Contains("--initialize-development", StringComparer.Ordinal))
{
    var pathIndex = Array.IndexOf(args, "--development-accounts");
    if (pathIndex < 0 || pathIndex + 1 >= args.Length)
        throw new ArgumentException("初始化需指定 --development-accounts <path>。");
    await DevelopmentInitializer.RunAsync(app.Services,
        builder.Configuration.GetConnectionString("BoardTrace")!, args[pathIndex + 1],
        args.Contains("--reset-database", StringComparer.Ordinal));
    return;
}
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>().Database.EnsureCreatedAsync();
app.MapGet("/health", async (BoardTraceDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { application = "BoardTrace", status = "ok", database = "sql-server" }) : Results.StatusCode(503));
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapAuth();
app.MapInspections();
app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "DELETE", "PATCH"], () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }
