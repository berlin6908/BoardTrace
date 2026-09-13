using Microsoft.AspNetCore.Identity;

namespace BoardTrace.Server.Identity;

public sealed class BoardTraceUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public string? StationId { get; set; }
}
