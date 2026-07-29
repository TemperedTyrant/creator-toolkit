using Microsoft.AspNetCore.Identity;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? ActivatedAtUtc { get; set; }

    public static ApplicationUser CreateInitialOwner(
        string userName,
        string? displayName,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName,
            IsEnabled = true,
            LockoutEnabled = true,
            CreatedAtUtc = createdAtUtc,
            ActivatedAtUtc = createdAtUtc,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
    }

    public static ApplicationUser CreatePending(
        string userName,
        string? displayName,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName,
            IsEnabled = false,
            LockoutEnabled = true,
            CreatedAtUtc = createdAtUtc,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
    }

    public void Activate(DateTimeOffset activatedAtUtc)
    {
        if (ActivatedAtUtc is not null)
        {
            throw new InvalidOperationException("The account is already active.");
        }

        if (activatedAtUtc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activatedAtUtc),
                "An account cannot be activated before it was created.");
        }

        ActivatedAtUtc = activatedAtUtc;
        IsEnabled = true;
    }
}
