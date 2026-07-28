using Microsoft.AspNetCore.Identity;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? ActivatedAtUtc { get; set; }
}
