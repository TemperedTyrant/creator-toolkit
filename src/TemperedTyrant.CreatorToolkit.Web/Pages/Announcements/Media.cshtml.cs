using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Announcements;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
[SensitiveSecurityHeaderProfile]
public sealed class MediaModel(IAnnouncementService announcementService) : PageModel
{
    public async Task<IActionResult> OnGetAsync(
        Guid announcementId,
        Guid mediaId,
        CancellationToken cancellationToken)
    {
        AnnouncementMediaContent? media = await announcementService.GetMediaContentAsync(
            announcementId,
            mediaId,
            cancellationToken);
        if (media is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentDisposition = $"inline; filename=\"{media.GeneratedFileName}\"";
        byte[] bytes = media.Bytes;
        Response.OnCompleted(
            () =>
            {
                CryptographicOperations.ZeroMemory(bytes);
                return Task.CompletedTask;
            });
        return new FileContentResult(bytes, media.ContentType)
        {
            EnableRangeProcessing = false,
        };
    }
}
