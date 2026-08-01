using System.Security.Claims;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

namespace TemperedTyrant.CreatorToolkit.Web.Announcements;

internal static class AnnouncementPageUser
{
    internal static Guid? GetActorUserId(ClaimsPrincipal user)
    {
        return Guid.TryParse(
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            out Guid actorUserId)
            ? actorUserId
            : null;
    }

    internal static bool CanEdit(ClaimsPrincipal user)
    {
        return user.IsInRole(SystemRoles.Owner)
            || user.IsInRole(SystemRoles.Admin)
            || user.IsInRole(SystemRoles.Editor);
    }
}
