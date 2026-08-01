using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;

namespace TemperedTyrant.CreatorToolkit.Web.Discord;

public sealed class DiscordPublicationResultStore(TimeProvider timeProvider)
{
    private const int MaximumEntries = 100;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> entries = [];

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

    internal void Put(Guid actorUserId, DiscordPublicationResult result)
    {
        lock (gate)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            RemoveExpired(now);
            while (entries.Count >= MaximumEntries)
            {
                Guid oldest = entries.MinBy(value => value.Value.ExpiresAtUtc).Key;
                entries.Remove(oldest);
            }

            entries[result.SubmissionId] = new Entry(actorUserId, result, now + Lifetime);
        }
    }

    internal DiscordPublicationResult? Take(Guid actorUserId, Guid submissionId)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(submissionId, out Entry? entry)
                || entry.ActorUserId != actorUserId)
            {
                return null;
            }

            entries.Remove(submissionId);
            if (entry.ExpiresAtUtc <= timeProvider.GetUtcNow())
            {
                return null;
            }

            return entry.Result;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (Guid id in entries
            .Where(value => value.Value.ExpiresAtUtc <= now)
            .Select(value => value.Key)
            .ToArray())
        {
            entries.Remove(id);
        }
    }

    private sealed record Entry(
        Guid ActorUserId,
        DiscordPublicationResult Result,
        DateTimeOffset ExpiresAtUtc);
}
