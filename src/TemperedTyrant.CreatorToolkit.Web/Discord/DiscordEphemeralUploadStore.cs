using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

namespace TemperedTyrant.CreatorToolkit.Web.Discord;

public sealed class DiscordEphemeralUploadStore(TimeProvider timeProvider) : IDisposable
{
    internal const int MaximumItems = 16;
    internal const int MaximumItemsPerActor = 2;
    internal const long MaximumTotalBytes = 64L * 1024 * 1024;
    internal const long MaximumBytesPerActor = 16L * 1024 * 1024;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long totalBytes;
    private bool disposed;

    internal int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    internal long TotalBytes
    {
        get
        {
            lock (gate)
            {
                return totalBytes;
            }
        }
    }

    internal DiscordStagedUpload Stage(
        DiscordEphemeralUploadBinding binding,
        DiscordValidatedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Bytes.Length is < 1 or > DiscordImageValidation.MaximumBytes)
        {
            throw new DiscordEphemeralUploadCapacityException();
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            DateTimeOffset now = timeProvider.GetUtcNow();
            RemoveExpired(now);
            RemoveMatchingSubmission(binding.ActorUserId, binding.PublicationSubmissionId);

            int actorItems = entries.Values.Count(value =>
                value.Binding.ActorUserId == binding.ActorUserId);
            long actorBytes = entries.Values
                .Where(value => value.Binding.ActorUserId == binding.ActorUserId)
                .Sum(value => (long)value.Image.Bytes.Length);
            if (actorItems >= MaximumItemsPerActor
                || actorBytes + image.Bytes.Length > MaximumBytesPerActor
                || entries.Count >= MaximumItems
                || totalBytes + image.Bytes.Length > MaximumTotalBytes)
            {
                throw new DiscordEphemeralUploadCapacityException();
            }

            string handle;
            do
            {
                handle = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            }
            while (entries.ContainsKey(handle));

            var metadata = new DiscordStagedUpload(
                handle,
                image.OutboundFileName,
                Format(image.ContentType),
                image.Bytes.Length,
                image.Spoiler,
                image.EmbedPlacement,
                !string.IsNullOrEmpty(image.AltText));
            entries.Add(handle, new Entry(binding, image, metadata, now + Lifetime));
            totalBytes += image.Bytes.Length;
            return metadata;
        }
    }

    internal DiscordStagedUpload? GetMetadata(
        string handle,
        DiscordEphemeralUploadBinding binding)
    {
        lock (gate)
        {
            if (disposed)
            {
                return null;
            }

            RemoveExpired(timeProvider.GetUtcNow());
            return entries.TryGetValue(handle, out Entry? entry)
                && entry.Binding == binding
                    ? entry.Metadata
                    : null;
        }
    }

    internal DiscordEphemeralUploadLease? Consume(
        string handle,
        DiscordEphemeralUploadBinding binding)
    {
        lock (gate)
        {
            if (disposed)
            {
                return null;
            }

            RemoveExpired(timeProvider.GetUtcNow());
            if (!entries.TryGetValue(handle, out Entry? entry)
                || entry.Binding != binding)
            {
                return null;
            }

            entries.Remove(handle);
            totalBytes -= entry.Image.Bytes.Length;
            return new DiscordEphemeralUploadLease(entry.Image, entry.Metadata);
        }
    }

    internal DiscordValidatedImage? Copy(
        string handle,
        DiscordEphemeralUploadBinding binding)
    {
        lock (gate)
        {
            if (disposed)
            {
                return null;
            }

            RemoveExpired(timeProvider.GetUtcNow());
            if (!entries.TryGetValue(handle, out Entry? entry)
                || entry.Binding != binding)
            {
                return null;
            }

            return entry.Image with { Bytes = entry.Image.Bytes.ToArray() };
        }
    }

    internal bool Remove(string handle, DiscordEphemeralUploadBinding binding)
    {
        lock (gate)
        {
            if (disposed
                || !entries.TryGetValue(handle, out Entry? entry)
                || entry.Binding != binding)
            {
                return false;
            }

            RemoveEntry(handle, entry);
            return true;
        }
    }

    internal void RemoveForSubmission(Guid actorUserId, Guid submissionId)
    {
        lock (gate)
        {
            if (!disposed)
            {
                RemoveMatchingSubmission(actorUserId, submissionId);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            foreach (Entry entry in entries.Values)
            {
                CryptographicOperations.ZeroMemory(entry.Image.Bytes);
            }

            entries.Clear();
            totalBytes = 0;
            disposed = true;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach ((string handle, Entry entry) in entries
            .Where(value => value.Value.ExpiresAtUtc <= now)
            .ToArray())
        {
            RemoveEntry(handle, entry);
        }
    }

    private void RemoveMatchingSubmission(Guid actorUserId, Guid submissionId)
    {
        foreach ((string handle, Entry entry) in entries
            .Where(value =>
                value.Value.Binding.ActorUserId == actorUserId
                && value.Value.Binding.PublicationSubmissionId == submissionId)
            .ToArray())
        {
            RemoveEntry(handle, entry);
        }
    }

    private void RemoveEntry(string handle, Entry entry)
    {
        entries.Remove(handle);
        totalBytes -= entry.Image.Bytes.Length;
        CryptographicOperations.ZeroMemory(entry.Image.Bytes);
    }

    private static string Format(string contentType) => contentType switch
    {
        "image/jpeg" => "JPEG",
        "image/png" => "PNG",
        "image/webp" => "WebP",
        "image/gif" => "GIF",
        _ => "Image",
    };

    private sealed record Entry(
        DiscordEphemeralUploadBinding Binding,
        DiscordValidatedImage Image,
        DiscordStagedUpload Metadata,
        DateTimeOffset ExpiresAtUtc);
}

internal sealed record DiscordEphemeralUploadBinding(
    Guid ActorUserId,
    Guid AnnouncementId,
    long AnnouncementRevision,
    Guid ConnectionId,
    string GuildId,
    Guid PublicationSubmissionId,
    DiscordMessageMode Mode,
    bool Spoiler,
    bool EmbedPlacement);

internal sealed record DiscordStagedUpload(
    string Handle,
    string SafeFileName,
    string Format,
    int ByteSize,
    bool Spoiler,
    bool EmbedPlacement,
    bool HasAltText);

internal sealed class DiscordEphemeralUploadLease(
    DiscordValidatedImage image,
    DiscordStagedUpload metadata) : IDisposable
{
    private bool disposed;

    internal DiscordValidatedImage Image { get; } = image;

    internal DiscordStagedUpload Metadata { get; } = metadata;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(Image.Bytes);
        disposed = true;
    }
}

internal sealed class DiscordEphemeralUploadCapacityException : Exception;
