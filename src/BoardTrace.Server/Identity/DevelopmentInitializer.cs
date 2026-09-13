using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BoardTrace.Server.Identity;

public static class DevelopmentInitializer
{
    private sealed record SeedAccount(string UserName, string DisplayName, string Role, string? StationId);

    private static readonly SeedAccount[] Accounts =
    [
        new("operator", "模拟操作员", "Operator", null),
        new("process", "模拟工艺工程师", "ProcessEngineer", null),
        new("quality", "模拟质量工程师", "QualityEngineer", null),
        new("station-01", "STATION-01", "Station", "STATION-01"),
        new("station-02", "STATION-02", "Station", "STATION-02")
    ];

    public static async Task RunAsync(IServiceProvider services, string connectionString, string accountsPath, bool reset)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(target.DataSource, @"(localdb)\BoardTrace", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(target.InitialCatalog, "BoardTrace", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("开发初始化只允许本地 BoardTrace 数据库。");
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BoardTraceDbContext>();
        if (reset) await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<BoardTraceUser>>();
        foreach (var role in Accounts.Select(account => account.Role).Distinct())
        {
            if (await roles.RoleExistsAsync(role)) continue;
            var result = await roles.CreateAsync(new IdentityRole(role));
            if (!result.Succeeded) throw new InvalidOperationException("无法创建开发角色。" + string.Join(";", result.Errors.Select(error => error.Description)));
        }

        var secrets = File.Exists(accountsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(accountsPath))!
            : new Dictionary<string, string>();
        foreach (var account in Accounts)
        {
            var user = await users.FindByNameAsync(account.UserName);
            if (user is null)
            {
                var password = "A!a1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                user = new BoardTraceUser { UserName = account.UserName, DisplayName = account.DisplayName, StationId = account.StationId };
                var created = await users.CreateAsync(user, password);
                if (!created.Succeeded) throw new InvalidOperationException("无法创建开发账号。" + string.Join(";", created.Errors.Select(error => error.Description)));
                secrets[account.UserName] = password;
            }
            else if (!secrets.ContainsKey(account.UserName))
                throw new InvalidOperationException($"{account.UserName} 已存在但凭据文件缺失；不会重设原密码。");
            if (!await users.IsInRoleAsync(user, account.Role))
            {
                var added = await users.AddToRoleAsync(user, account.Role);
                if (!added.Succeeded) throw new InvalidOperationException("无法绑定开发角色。" + string.Join(";", added.Errors.Select(error => error.Description)));
            }
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(accountsPath))!;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(accountsPath, JsonSerializer.Serialize(secrets, new JsonSerializerOptions { WriteIndented = true }));
        var stationDirectory = Path.Combine(directory, "stations");
        Directory.CreateDirectory(stationDirectory);
        foreach (var account in Accounts.Where(account => account.StationId is not null))
            await File.WriteAllTextAsync(Path.Combine(stationDirectory, account.StationId + ".json"),
                JsonSerializer.Serialize(new StationCredentials(account.UserName, secrets[account.UserName]),
                    new JsonSerializerOptions { WriteIndented = true }));
    }
}
