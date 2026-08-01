using System.Security.Cryptography;
using TemperedTyrant.CreatorToolkit.Core.Announcements;

namespace TemperedTyrant.CreatorToolkit.Web.Announcements;

internal static class AnnouncementMediaForm
{
    internal static async Task<IReadOnlyList<AnnouncementMediaUpload>> ReadUploadsAsync(
        IReadOnlyList<IFormFile> files,
        IReadOnlyList<string?> altTexts,
        IReadOnlyList<bool> spoilers,
        IReadOnlyList<AnnouncementMediaPresentation> presentations,
        IReadOnlyList<int> sortOrders,
        int fallbackStartingOrder,
        CancellationToken cancellationToken)
    {
        if (files.Count > AnnouncementMediaAsset.MaximumAssetCount)
        {
            throw new AnnouncementMediaFormException(
                "An announcement can contain at most four images.");
        }

        var uploads = new List<AnnouncementMediaUpload>(files.Count);
        long total = 0;
        try
        {
            for (int index = 0; index < files.Count; index++)
            {
                IFormFile file = files[index];
                if (file.Length is < 1 or > AnnouncementMediaAsset.MaximumCombinedBytes)
                {
                    throw new AnnouncementMediaFormException(
                        "Each image must be non-empty and no larger than 8 MiB.");
                }

                await using Stream source = file.OpenReadStream();
                using var memory = new MemoryStream();
                byte[] buffer = new byte[64 * 1024];
                byte[]? bytes = null;
                try
                {
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                        if (total > AnnouncementMediaAsset.MaximumCombinedBytes)
                        {
                            throw new AnnouncementMediaFormException(
                                "Combined announcement images must be no larger than 8 MiB.");
                        }

                        await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }

                    bytes = memory.ToArray();
                    uploads.Add(new AnnouncementMediaUpload(
                        bytes,
                        Path.GetFileName(file.FileName),
                        file.ContentType,
                        index < altTexts.Count ? altTexts[index] : null,
                        index < spoilers.Count && spoilers[index],
                        index < presentations.Count
                            ? presentations[index]
                            : AnnouncementMediaPresentation.Attachment,
                        index < sortOrders.Count ? sortOrders[index] : fallbackStartingOrder + index));
                    bytes = null;
                }
                finally
                {
                    if (bytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }

                    CryptographicOperations.ZeroMemory(buffer);
                    if (memory.TryGetBuffer(out ArraySegment<byte> memoryBuffer))
                    {
                        CryptographicOperations.ZeroMemory(memoryBuffer.AsSpan());
                    }
                }
            }

            return uploads;
        }
        catch
        {
            Zero(uploads);
            throw;
        }
    }

    internal static void Zero(IEnumerable<AnnouncementMediaUpload> uploads)
    {
        foreach (AnnouncementMediaUpload upload in uploads)
        {
            CryptographicOperations.ZeroMemory(upload.Bytes);
        }
    }
}

internal sealed class AnnouncementMediaFormException(string message) : Exception(message);

public sealed class AnnouncementMediaEditInput
{
    public Guid Id { get; set; }

    public long Revision { get; set; }

    public int SortOrder { get; set; }

    public string? AltText { get; set; }

    public bool IsSpoiler { get; set; }

    public AnnouncementMediaPresentation Presentation { get; set; }

    public bool Remove { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public int ByteLength { get; set; }

    internal AnnouncementMediaEdit ToDomain() => new(
        Id,
        Revision,
        SortOrder,
        AltText,
        IsSpoiler,
        Presentation,
        Remove);
}

public sealed record AnnouncementComposerViewModel(
    string Title,
    string MessageContent,
    IReadOnlyList<AnnouncementMediaEditInput> ExistingMedia,
    bool ShowTitle,
    bool AllowUploads,
    Guid? AnnouncementId = null,
    IReadOnlyCollection<Guid>? SelectedMediaIds = null);
