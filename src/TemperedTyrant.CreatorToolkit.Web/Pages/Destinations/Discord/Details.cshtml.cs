using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Web.Announcements;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Pages.Destinations.Discord;

[Authorize(Policy = AuthorizationPolicies.ApplicationAccess)]
[SensitiveSecurityHeaderProfile]
[RequestSizeLimit(64 * 1024)]
public sealed class DetailsModel(IDiscordConfigurationService discord) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? GuildId { get; set; }

    [BindProperty]
    public string BotToken { get; set; } = string.Empty;

    [BindProperty]
    public IReadOnlyList<string> ChannelIds { get; set; } = [];

    public DiscordConnectionDetails? Details { get; private set; }

    public IReadOnlyList<DiscordGuild> Guilds { get; private set; } = [];

    public DiscordGuildDiscovery? Discovery { get; private set; }

    public bool CanManage => User.IsInRole(SystemRoles.Owner) || User.IsInRole(SystemRoles.Admin);

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }

        StatusMessage = Request.Query["notice"].ToString() switch
        {
            "created" => "The Discord bot connection was created.",
            "saved" => "The selected channels were saved.",
            "updated" => "The connection was updated.",
            "test-success" => "The destination test message was sent.",
            "test-failed" => "The destination test did not succeed. Review the destination and bot permissions.",
            "stale" => "The connection changed. Reload and try again.",
            "failed" => "The Discord operation did not succeed. Review the connection and try again.",
            _ => null,
        } ?? StatusMessage;
        return Page();
    }

    public Task<IActionResult> OnPostReplaceTokenAsync(long revision, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(BotToken))
        {
            BotToken = string.Empty;
            return Task.FromResult<IActionResult>(
                RedirectToPage(new { id = Id, notice = "failed" }));
        }

        return RunAsync(
            (actor, ct) => discord.ReplaceTokenAsync(Id, revision, BotToken, actor, ct),
            token);
    }

    public Task<IActionResult> OnPostRefreshIdentityAsync(long revision, CancellationToken token) =>
        RunAsync((actor, ct) => discord.RefreshIdentityAsync(Id, revision, actor, ct), token);

    public Task<IActionResult> OnPostEnableAsync(long revision, bool enabled, CancellationToken token) =>
        RunAsync((actor, ct) => discord.SetConnectionEnabledAsync(Id, revision, enabled, actor, ct), token);

    public async Task<IActionResult> OnPostSaveChannelsAsync(CancellationToken token)
    {
        if (!CanManage || string.IsNullOrEmpty(GuildId))
        {
            return CanManage ? BadRequest() : Forbid();
        }

        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        DiscordOperationResult result;
        try
        {
            result = await discord.SaveDestinationsAsync(
                Id,
                GuildId,
                ChannelIds,
                actor.Value,
                token);
        }
        catch (DiscordServerInformationException)
        {
            return RedirectToPage(new { id = Id, GuildId });
        }

        return RedirectToPage(new
        {
            id = Id,
            guildId = GuildId,
            notice = Notice(result),
        });
    }

    public async Task<IActionResult> OnPostDestinationEnableAsync(
        Guid destinationId,
        long revision,
        bool enabled,
        CancellationToken token)
    {
        if (!CanManage)
        {
            return Forbid();
        }

        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        DiscordOperationResult result = await discord.SetDestinationEnabledAsync(
            destinationId,
            revision,
            enabled,
            actor.Value,
            token);
        return RedirectToPage(new { id = Id, notice = Notice(result) });
    }

    public async Task<IActionResult> OnPostDeleteDestinationAsync(
        Guid destinationId,
        long revision,
        CancellationToken token)
    {
        if (!CanManage)
        {
            return Forbid();
        }

        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        DiscordOperationResult result = await discord.DeleteDestinationAsync(
            destinationId,
            revision,
            actor.Value,
            token);
        return RedirectToPage(new { id = Id, notice = Notice(result) });
    }

    public async Task<IActionResult> OnPostTestAsync(
        Guid destinationId,
        long revision,
        bool confirmed,
        CancellationToken token)
    {
        if (!CanManage || !confirmed)
        {
            return CanManage ? BadRequest() : Forbid();
        }

        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        DiscordDeliveryResult result = await discord.SendDestinationTestAsync(
            destinationId,
            revision,
            actor.Value,
            token);
        return RedirectToPage(new
        {
            id = Id,
            notice = result.Status == DiscordDeliveryStatus.Success ? "test-success" : "test-failed",
        });
    }

    public async Task<IActionResult> OnPostDeleteAsync(
        long revision,
        bool confirmed,
        CancellationToken token)
    {
        if (!CanManage || !confirmed)
        {
            return CanManage ? BadRequest() : Forbid();
        }

        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            return Forbid();
        }

        DiscordOperationResult result = await discord.DeleteConnectionAsync(
            Id,
            revision,
            actor.Value,
            token);
        return result.Status == DiscordOperationStatus.Succeeded
            ? RedirectToPage("/Destinations")
            : RedirectToPage(new { id = Id, notice = Notice(result) });
    }

    private async Task<IActionResult> RunAsync(
        Func<Guid, CancellationToken, Task<DiscordOperationResult>> operation,
        CancellationToken token)
    {
        if (!CanManage)
        {
            BotToken = string.Empty;
            return Forbid();
        }

        Guid? actor = AnnouncementPageUser.GetActorUserId(User);
        if (actor is null)
        {
            BotToken = string.Empty;
            return Forbid();
        }

        DiscordOperationResult result = await operation(actor.Value, token);
        BotToken = string.Empty;
        return RedirectToPage(new { id = Id, notice = Notice(result) });
    }

    private async Task<bool> LoadAsync(CancellationToken token)
    {
        Details = await discord.GetAsync(Id, token);
        if (Details is null)
        {
            return false;
        }

        if (!CanManage)
        {
            return true;
        }

        try
        {
            Guilds = await discord.ListGuildsAsync(Id, token);
            if (!string.IsNullOrEmpty(GuildId))
            {
                Discovery = await discord.DiscoverGuildAsync(Id, GuildId, token);
            }
        }
        catch (DiscordServerInformationException exception)
        {
            StatusMessage = exception.DiagnosticReference is null
                ? exception.SafeMessage
                : $"{exception.SafeMessage} Diagnostic reference: {exception.DiagnosticReference}";
        }
        catch (DiscordApiAuthenticationException)
        {
            StatusMessage = "Discord bot authentication failed.";
        }
        catch (DiscordApiUnavailableException)
        {
            StatusMessage = "Discord is temporarily unavailable.";
        }

        return true;
    }

    private static string Notice(DiscordOperationResult result) => result.Status switch
    {
        DiscordOperationStatus.Succeeded => "updated",
        DiscordOperationStatus.StaleRevision => "stale",
        _ => "failed",
    };
}
