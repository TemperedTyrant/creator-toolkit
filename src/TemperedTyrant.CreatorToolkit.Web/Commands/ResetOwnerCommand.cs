using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Web.Configuration;

namespace TemperedTyrant.CreatorToolkit.Web.Commands;

public static class ResetOwnerCommand
{
    public const string Name = "reset-owner";
    public const string NonInteractiveFlag = "--yes";
    private const int MaximumHashCollisionAttempts = 3;
    private const string ConfirmationPhrase = "RESET OWNER";

    public static async Task<int> RunAsync(
        IServiceProvider services,
        CreatorToolkitOptions options,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool nonInteractive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            if (!nonInteractive)
            {
                await output.WriteLineAsync(
                    "This invalidates current Owner sessions and issues a one-time recovery capability.");
                await output.WriteAsync($"Type {ConfirmationPhrase} to continue: ");
                await output.FlushAsync(cancellationToken);
                string? confirmation = await input.ReadLineAsync(cancellationToken);
                if (!string.Equals(
                        confirmation,
                        ConfirmationPhrase,
                        StringComparison.Ordinal))
                {
                    await error.WriteLineAsync("Owner recovery was cancelled.");
                    return 1;
                }
            }

            await services
                .GetRequiredService<MigrationCoordinator>()
                .EnsureCurrentForAdministrativeCommandAsync(cancellationToken);

            for (int attempt = 0; attempt < MaximumHashCollisionAttempts; attempt++)
            {
                string rawCapability = WebEncoders.Base64UrlEncode(
                    RandomNumberGenerator.GetBytes(32));
                byte[] capabilityHash = SHA256.HashData(
                    Encoding.UTF8.GetBytes(rawCapability));
                try
                {
                    await using AsyncServiceScope scope = services.CreateAsyncScope();
                    await scope.ServiceProvider
                        .GetRequiredService<OwnerRecoveryIssuer>()
                        .IssueAsync(capabilityHash, cancellationToken);
                    await WriteCapabilityAsync(output, options.PublicUrl, rawCapability);
                    return 0;
                }
                catch (DbUpdateException) when (
                    attempt + 1 < MaximumHashCollisionAttempts)
                {
                    // A fresh capability and service scope are used for the bounded retry.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 1;
        }
        catch (Exception)
        {
            // The raw capability, paths, exception text, and database details are excluded.
        }

        await error.WriteLineAsync("Owner recovery capability generation failed.");
        return 1;
    }

    private static async Task WriteCapabilityAsync(
        TextWriter output,
        Uri? publicUrl,
        string rawCapability)
    {
        const string route = "/Account/RecoverOwner";
        if (publicUrl is not null)
        {
            Uri recoveryUrl = new(publicUrl, route);
            await output.WriteLineAsync(
                $"{recoveryUrl.AbsoluteUri}#token={rawCapability}");
            return;
        }

        await output.WriteLineAsync(route);
        await output.WriteLineAsync(rawCapability);
    }
}
