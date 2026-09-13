namespace BoardTrace.Contracts;

public sealed record CurrentUser(string Id, string UserName, string DisplayName,
    IReadOnlyList<string> Roles, string? StationId);

public sealed record LoginRequest(string UserName, string Password);

public sealed record StationCredentials(string UserName, string Password);
