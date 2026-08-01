using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using TemperedTyrant.CreatorToolkit.Core.Announcements;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Announcements;

internal sealed class AnnouncementMediaProtector(IDataProtectionProvider provider)
{
    internal const int MaximumCiphertextBytes = 9 * 1024 * 1024;
    private const string Purpose =
        "TemperedTyrant.CreatorToolkit.AnnouncementMedia.v1";

    internal byte[] Protect(Guid announcementId, Guid mediaId, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length is < 1 or > AnnouncementMediaAsset.MaximumCombinedBytes)
        {
            throw new AnnouncementMediaValidationException("The image size is invalid.");
        }

        byte[] copy = plaintext.ToArray();
        try
        {
            byte[] ciphertext = CreateProtector(announcementId, mediaId).Protect(copy);
            if (ciphertext.Length > MaximumCiphertextBytes)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                throw new AnnouncementMediaValidationException("The image is too large to store safely.");
            }

            return ciphertext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    internal byte[] Unprotect(AnnouncementMediaAsset media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.ProtectedContent.Length is < 1 or > MaximumCiphertextBytes
            || media.ByteLength is < 1 or > AnnouncementMediaAsset.MaximumCombinedBytes)
        {
            throw new AnnouncementMediaUnavailableException();
        }

        try
        {
            byte[] plaintext = CreateProtector(media.AnnouncementId, media.Id)
                .Unprotect(media.ProtectedContent);
            byte[] digest = SHA256.HashData(plaintext);
            bool valid = plaintext.Length == media.ByteLength
                && CryptographicOperations.FixedTimeEquals(digest, media.Sha256Digest);
            CryptographicOperations.ZeroMemory(digest);
            if (!valid)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new AnnouncementMediaUnavailableException();
            }

            return plaintext;
        }
        catch (CryptographicException)
        {
            throw new AnnouncementMediaUnavailableException();
        }
    }

    private IDataProtector CreateProtector(Guid announcementId, Guid mediaId) =>
        provider.CreateProtector(
            Purpose,
            announcementId.ToString("N"),
            mediaId.ToString("N"));
}

internal static class AnnouncementMediaValidation
{
    internal static ValidatedAnnouncementMedia Validate(AnnouncementMediaUpload upload, Guid mediaId)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (upload.Bytes.Length is < 1 or > AnnouncementMediaAsset.MaximumCombinedBytes)
        {
            throw new AnnouncementMediaValidationException("Each image must be no larger than 8 MiB.");
        }

        string extension = Path.GetExtension(Path.GetFileName(upload.FileName)).ToLowerInvariant();
        string expectedContentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => throw new AnnouncementMediaValidationException(
                "Use a JPEG, PNG, WebP, or GIF image."),
        };
        if (!string.Equals(upload.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase)
            || !HasSignature(upload.Bytes, expectedContentType))
        {
            throw new AnnouncementMediaValidationException(
                "The image type does not match its validated file content.");
        }

        string? altText = string.IsNullOrWhiteSpace(upload.AltText)
            ? null
            : upload.AltText.Trim();
        if (altText?.Length > AnnouncementMediaAsset.MaximumAltTextLength)
        {
            throw new AnnouncementMediaValidationException("Image alt text is too long.");
        }

        string safeExtension = expectedContentType == "image/jpeg" ? ".jpg" : extension;
        return new ValidatedAnnouncementMedia(
            upload.Bytes,
            expectedContentType,
            $"announcement-{mediaId:N}{safeExtension}",
            altText,
            upload.IsSpoiler,
            upload.Presentation,
            upload.SortOrder,
            SHA256.HashData(upload.Bytes));
    }

    private static bool HasSignature(ReadOnlySpan<byte> bytes, string contentType) =>
        contentType switch
        {
            "image/jpeg" => bytes.Length >= 3
                && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            "image/png" => bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            "image/webp" => bytes.Length >= 12
                && bytes[..4].SequenceEqual("RIFF"u8)
                && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
}

internal sealed record ValidatedAnnouncementMedia(
    byte[] Bytes,
    string ContentType,
    string GeneratedFileName,
    string? AltText,
    bool IsSpoiler,
    AnnouncementMediaPresentation Presentation,
    int SortOrder,
    byte[] Sha256Digest);

internal sealed class AnnouncementMediaValidationException(string message) : Exception(message);

internal sealed class AnnouncementMediaUnavailableException : Exception;
