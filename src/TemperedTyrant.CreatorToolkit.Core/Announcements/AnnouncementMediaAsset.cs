using System.Security.Cryptography;

namespace TemperedTyrant.CreatorToolkit.Core.Announcements;

public sealed class AnnouncementMediaAsset
{
    public const int MaximumAssetCount = 4;
    public const int MaximumCombinedBytes = 8 * 1024 * 1024;
    public const int MaximumAltTextLength = 1_024;

    private AnnouncementMediaAsset()
    {
    }

    public Guid Id { get; private set; }

    public Guid AnnouncementId { get; private set; }

    public Announcement Announcement { get; private set; } = null!;

    public int SortOrder { get; private set; }

    public byte[] ProtectedContent { get; private set; } = [];

    public string ContentType { get; private set; } = string.Empty;

    public int ByteLength { get; private set; }

    public byte[] Sha256Digest { get; private set; } = [];

    public string GeneratedFileName { get; private set; } = string.Empty;

    public string? AltText { get; private set; }

    public bool IsSpoiler { get; private set; }

    public AnnouncementMediaPresentation Presentation { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Revision { get; private set; }

    public static AnnouncementMediaAsset Create(
        Guid id,
        Guid announcementId,
        int sortOrder,
        byte[] protectedContent,
        string contentType,
        int byteLength,
        byte[] sha256Digest,
        string generatedFileName,
        string? altText,
        bool isSpoiler,
        AnnouncementMediaPresentation presentation,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(announcementId, Guid.Empty);
        if (sortOrder is < 0 or >= MaximumAssetCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }
        ArgumentNullException.ThrowIfNull(protectedContent);
        ArgumentNullException.ThrowIfNull(sha256Digest);
        if (protectedContent.Length == 0 || byteLength is < 1 or > MaximumCombinedBytes
            || sha256Digest.Length != SHA256.HashSizeInBytes
            || !Enum.IsDefined(presentation))
        {
            throw new ArgumentException("The media content is invalid.");
        }

        return new AnnouncementMediaAsset
        {
            Id = id,
            AnnouncementId = announcementId,
            SortOrder = sortOrder,
            ProtectedContent = protectedContent,
            ContentType = Require(contentType, 32),
            ByteLength = byteLength,
            Sha256Digest = sha256Digest,
            GeneratedFileName = Require(generatedFileName, 80),
            AltText = NormalizeAltText(altText),
            IsSpoiler = isSpoiler,
            Presentation = presentation,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime(),
            Revision = 1,
        };
    }

    public bool UpdateMetadata(
        long expectedRevision,
        int sortOrder,
        string? altText,
        bool isSpoiler,
        AnnouncementMediaPresentation presentation,
        DateTimeOffset now)
    {
        if (Revision != expectedRevision)
        {
            return false;
        }

        if (sortOrder is < 0 or >= MaximumAssetCount || !Enum.IsDefined(presentation))
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }
        string? normalizedAltText = NormalizeAltText(altText);
        if (SortOrder == sortOrder
            && string.Equals(AltText, normalizedAltText, StringComparison.Ordinal)
            && IsSpoiler == isSpoiler
            && Presentation == presentation)
        {
            return true;
        }

        SortOrder = sortOrder;
        AltText = normalizedAltText;
        IsSpoiler = isSpoiler;
        Presentation = presentation;
        UpdatedAtUtc = now.ToUniversalTime();
        Revision = checked(Revision + 1);
        return true;
    }

    private static string Require(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException("The media metadata is invalid.");
        }

        return value;
    }

    private static string? NormalizeAltText(string? value)
    {
        string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > MaximumAltTextLength)
        {
            throw new ArgumentException("Image alt text is too long.");
        }

        return normalized;
    }
}

public enum AnnouncementMediaPresentation
{
    Attachment = 1,
    FeaturedImage = 2,
}

public sealed record AnnouncementMediaUpload(
    byte[] Bytes,
    string FileName,
    string ContentType,
    string? AltText,
    bool IsSpoiler,
    AnnouncementMediaPresentation Presentation,
    int SortOrder);

public sealed record AnnouncementMediaEdit(
    Guid Id,
    long Revision,
    int SortOrder,
    string? AltText,
    bool IsSpoiler,
    AnnouncementMediaPresentation Presentation,
    bool Remove);

public sealed record AnnouncementMediaChangeSet(
    IReadOnlyList<AnnouncementMediaEdit> Existing,
    IReadOnlyList<AnnouncementMediaUpload> Added)
{
    public static AnnouncementMediaChangeSet Empty { get; } = new([], []);
}
