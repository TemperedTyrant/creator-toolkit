using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Publications;

internal sealed class PublicationPayloadProtector(IDataProtectionProvider provider)
{
    internal const int MaximumPlaintextBytes = 12 * 1024 * 1024;
    internal const int MaximumCiphertextBytes = 13 * 1024 * 1024;
    private const string Purpose =
        "TemperedTyrant.CreatorToolkit.DurablePublicationPayload.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
    };

    internal PublicationPayload Protect(
        Guid publicationId,
        DiscordPublishRequest request,
        DateTimeOffset now)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        try
        {
            if (plaintext.Length > MaximumPlaintextBytes)
            {
                throw new DiscordPublicationValidationException(
                    "The reviewed Discord publication is too large to queue safely.");
            }

            byte[] ciphertext = CreateProtector(publicationId).Protect(plaintext);
            if (ciphertext.Length > MaximumCiphertextBytes)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                throw new DiscordPublicationValidationException(
                    "The reviewed Discord publication is too large to queue safely.");
            }

            return new PublicationPayload
            {
                PublicationId = publicationId,
                Ciphertext = ciphertext,
                PlaintextSize = plaintext.Length,
                CreatedAtUtc = now.ToUniversalTime(),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal DiscordPublishRequest Unprotect(PublicationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Ciphertext.Length is < 1 or > MaximumCiphertextBytes
            || payload.PlaintextSize is < 1 or > MaximumPlaintextBytes)
        {
            throw new PublicationPayloadException();
        }

        byte[] plaintext;
        try
        {
            plaintext = CreateProtector(payload.PublicationId).Unprotect(payload.Ciphertext);
        }
        catch (CryptographicException)
        {
            throw new PublicationPayloadException();
        }

        try
        {
            if (plaintext.Length != payload.PlaintextSize)
            {
                throw new PublicationPayloadException();
            }

            return JsonSerializer.Deserialize<DiscordPublishRequest>(plaintext, JsonOptions)
                ?? throw new PublicationPayloadException();
        }
        catch (JsonException)
        {
            throw new PublicationPayloadException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private IDataProtector CreateProtector(Guid publicationId) =>
        provider.CreateProtector(Purpose, publicationId.ToString("N"));
}

internal sealed class PublicationPayloadException : Exception;
