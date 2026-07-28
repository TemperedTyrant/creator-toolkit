using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.Setup;
using TemperedTyrant.CreatorToolkit.Web.Configuration;

namespace TemperedTyrant.CreatorToolkit.Web.Commands;

public static class BootstrapOwnerCommand
{
    public const string Name = "bootstrap-owner";
    private const int MaximumHashCollisionAttempts = 3;

    public static async Task<int> RunAsync(
        IServiceProvider services,
        CreatorToolkitOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
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
                    BootstrapCapabilityIssueResult result = await scope.ServiceProvider
                        .GetRequiredService<BootstrapCapabilityIssuer>()
                        .IssueAsync(capabilityHash, cancellationToken);

                    if (result == BootstrapCapabilityIssueResult.InstallationInitialized)
                    {
                        await error.WriteLineAsync(
                            "Bootstrap is permanently unavailable after initialization.");
                        return 1;
                    }

                    await WriteCapabilityAsync(output, options.PublicUrl, rawCapability);
                    return 0;
                }
                catch (DbUpdateException) when (
                    attempt + 1 < MaximumHashCollisionAttempts)
                {
                    // Nothing secret is included in the exception path or output. A fresh
                    // capability is generated before the bounded retry.
                }
            }

            await error.WriteLineAsync("Bootstrap capability generation failed.");
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 1;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Bootstrap capability generation failed.");
            return 1;
        }
    }

    private static async Task WriteCapabilityAsync(
        TextWriter output,
        Uri? publicUrl,
        string rawCapability)
    {
        if (publicUrl is not null)
        {
            Uri setupUrl = new(publicUrl, "/Setup");
            await output.WriteLineAsync($"{setupUrl.AbsoluteUri}#token={rawCapability}");
            return;
        }

        await output.WriteLineAsync("/Setup");
        await output.WriteLineAsync(rawCapability);
    }
}
