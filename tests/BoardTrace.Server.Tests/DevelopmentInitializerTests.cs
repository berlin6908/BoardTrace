using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Identity;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoardTrace.Server.Tests;

public sealed class DevelopmentInitializerTests
{
    [Fact]
    public async Task NamedFreshDatabaseCreatesUsableRoleCredentialsAndRetryPreservesAccounts()
    {
        var name = "BoardTrace_ReleaseTest_" + Guid.NewGuid().ToString("N");
        var connection = $@"Server=(localdb)\BoardTrace;Database={name};Integrated Security=true;TrustServerCertificate=true";
        var directory = Path.Combine(Path.GetTempPath(), name);
        var accountsPath = Path.Combine(directory, "accounts.json");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<BoardTraceDbContext>(options => options.UseSqlServer(connection));
        services.AddIdentity<BoardTraceUser, IdentityRole>().AddEntityFrameworkStores<BoardTraceDbContext>()
            .AddDefaultTokenProviders();
        await using var provider = services.BuildServiceProvider();
        try
        {
            await DevelopmentInitializer.RunAsync(provider, connection, accountsPath, reset: false);
            var credentialsBefore = await File.ReadAllTextAsync(accountsPath);
            var passwords = JsonSerializer.Deserialize<Dictionary<string, string>>(credentialsBefore)!;
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<BoardTraceUser>>();
            Assert.Equal(5, await db.Users.CountAsync());
            Assert.Equal(4, await db.Roles.CountAsync());
            var ids = new Dictionary<string, string>();
            foreach (var (userName, role) in new[] { ("operator", "Operator"), ("process", "ProcessEngineer"),
                         ("quality", "QualityEngineer"), ("station-01", "Station"), ("station-02", "Station") })
            {
                var user = (await users.FindByNameAsync(userName))!;
                Assert.True(await users.CheckPasswordAsync(user, passwords[userName]));
                Assert.Equal(role, Assert.Single(await users.GetRolesAsync(user)));
                ids.Add(userName, user.Id);
            }
            foreach (var station in new[] { "STATION-01", "STATION-02" })
            {
                var device = JsonSerializer.Deserialize<StationCredentials>(await File.ReadAllTextAsync(
                    Path.Combine(directory, "stations", station + ".json")))!;
                Assert.Equal(passwords[device.UserName], device.Password);
                Assert.Equal(station, (await users.FindByNameAsync(device.UserName))!.StationId);
            }
            Assert.Empty(await db.RecipeVersions.ToArrayAsync()); // Initialization cannot manufacture a publication.
            await DevelopmentInitializer.RunAsync(provider, connection, accountsPath, reset: false);
            Assert.Equal(credentialsBefore, await File.ReadAllTextAsync(accountsPath));
            Assert.Equal(ids.OrderBy(pair => pair.Key), (await db.Users.AsNoTracking().ToDictionaryAsync(user => user.UserName!, user => user.Id)).OrderBy(pair => pair.Key));
        }
        finally
        {
            // Only this test's generated database is removed; the existing BoardTrace database is never selected.
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>().Database.EnsureDeletedAsync();
            foreach (var path in new[] { accountsPath, Path.Combine(directory, "stations", "STATION-01.json"),
                         Path.Combine(directory, "stations", "STATION-02.json") })
                File.Delete(path);
            if (Directory.Exists(Path.Combine(directory, "stations"))) Directory.Delete(Path.Combine(directory, "stations"));
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData(@"Server=(localdb)\Other;Database=BoardTrace_Release;Integrated Security=true")]
    [InlineData(@"Server=(localdb)\BoardTrace;Database=UnrelatedDatabase;Integrated Security=true")]
    [InlineData(@"Server=(localdb)\BoardTrace;Database=BoardTrace_;Integrated Security=true")]
    public async Task RejectsAnUnrelatedInstanceOrDatabaseBeforeCreatingCredentials(string connection)
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var directory = Path.Combine(Path.GetTempPath(), "BoardTrace_Rejected_" + Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DevelopmentInitializer.RunAsync(provider,
            connection, Path.Combine(directory, "accounts.json"), reset: false));
        Assert.Contains("初始化只允许", error.Message);
        Assert.False(Directory.Exists(directory));
    }
}
