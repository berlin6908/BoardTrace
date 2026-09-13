using BoardTrace.Server.Storage;
using BoardTrace.Server.Inspections;
using Microsoft.EntityFrameworkCore;

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
var app = builder.Build();
app.UseExceptionHandler();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>().Database.EnsureCreatedAsync();
app.MapGet("/health", async (BoardTraceDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { application = "BoardTrace", status = "ok", database = "sql-server" }) : Results.StatusCode(503));
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapInspections();
app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "DELETE", "PATCH"], () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }
