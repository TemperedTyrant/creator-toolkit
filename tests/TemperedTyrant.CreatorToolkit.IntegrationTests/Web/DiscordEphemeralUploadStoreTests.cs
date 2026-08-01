using System.Security.Cryptography;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;
using TemperedTyrant.CreatorToolkit.Web.Discord;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed class DiscordEphemeralUploadStoreTests
{
    [Fact]
    public void StagedUploadIsOpaqueBoundActorIsolatedAndSingleUse()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new DiscordEphemeralUploadStore(time);
        Guid actor = Guid.NewGuid();
        DiscordEphemeralUploadBinding binding = Binding(actor);
        DiscordValidatedImage image = Image([1, 2, 3, 4]);

        DiscordStagedUpload staged = store.Stage(binding, image);

        Assert.Matches("^[A-Za-z0-9_-]{43}$", staged.Handle);
        Assert.DoesNotContain(actor.ToString("N"), staged.Handle, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(staged.Handle, store.Stage(Binding(Guid.NewGuid()), Image([5, 6, 7, 8])).Handle);
        Assert.Null(store.GetMetadata(staged.Handle, binding with { ActorUserId = Guid.NewGuid() }));
        Assert.Null(store.Consume("forged-handle", binding));
        Assert.Null(store.Consume(staged.Handle, binding with { AnnouncementRevision = 2 }));

        using DiscordEphemeralUploadLease lease = Assert.IsType<DiscordEphemeralUploadLease>(
            store.Consume(staged.Handle, binding));
        Assert.Equal([1, 2, 3, 4], lease.Image.Bytes);
        Assert.Null(store.Consume(staged.Handle, binding));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void BindingCoversAnnouncementSubmissionConnectionGuildAndImageMode()
    {
        using var store = new DiscordEphemeralUploadStore(TimeProvider.System);
        DiscordEphemeralUploadBinding binding = Binding(Guid.NewGuid());
        DiscordStagedUpload staged = store.Stage(binding, Image([1, 2, 3, 4]));

        Assert.Null(store.GetMetadata(staged.Handle, binding with { AnnouncementId = Guid.NewGuid() }));
        Assert.Null(store.GetMetadata(staged.Handle, binding with { PublicationSubmissionId = Guid.NewGuid() }));
        Assert.Null(store.GetMetadata(staged.Handle, binding with { ConnectionId = Guid.NewGuid() }));
        Assert.Null(store.GetMetadata(staged.Handle, binding with { GuildId = "900000000000000009" }));
        Assert.Null(store.GetMetadata(staged.Handle, binding with { Mode = DiscordMessageMode.Plain }));
        Assert.Null(store.GetMetadata(staged.Handle, binding with { Spoiler = false }));
        Assert.Null(store.GetMetadata(staged.Handle, binding with { EmbedPlacement = false }));
        Assert.NotNull(store.GetMetadata(staged.Handle, binding));
    }

    [Fact]
    public void ExpiryIsLazyAndZeroesRemovedBytes()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new DiscordEphemeralUploadStore(time);
        byte[] bytes = [1, 2, 3, 4];
        DiscordEphemeralUploadBinding binding = Binding(Guid.NewGuid());
        DiscordStagedUpload staged = store.Stage(binding, Image(bytes));

        time.Advance(DiscordEphemeralUploadStore.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Null(store.GetMetadata(staged.Handle, binding));
        Assert.Equal([0, 0, 0, 0], bytes);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.TotalBytes);
    }

    [Fact]
    public void PerActorAndGlobalItemBoundsFailClosed()
    {
        using var store = new DiscordEphemeralUploadStore(TimeProvider.System);
        Guid actor = Guid.NewGuid();
        _ = store.Stage(Binding(actor), Image([1, 2, 3, 4]));
        _ = store.Stage(
            Binding(actor) with { PublicationSubmissionId = Guid.NewGuid() },
            Image([5, 6, 7, 8]));
        Assert.Throws<DiscordEphemeralUploadCapacityException>(() => store.Stage(
            Binding(actor) with { PublicationSubmissionId = Guid.NewGuid() },
            Image([9, 10, 11, 12])));

        for (int index = store.Count; index < DiscordEphemeralUploadStore.MaximumItems; index++)
        {
            _ = store.Stage(Binding(Guid.NewGuid()), Image([1, 2, 3, 4]));
        }

        Assert.Equal(DiscordEphemeralUploadStore.MaximumItems, store.Count);
        Assert.Throws<DiscordEphemeralUploadCapacityException>(() =>
            store.Stage(Binding(Guid.NewGuid()), Image([1, 2, 3, 4])));
    }

    [Fact]
    public void GlobalByteBoundAndShutdownDisposalZeroAllBuffers()
    {
        var store = new DiscordEphemeralUploadStore(TimeProvider.System);
        var buffers = new List<byte[]>();
        for (int index = 0; index < 8; index++)
        {
            byte[] bytes = GC.AllocateUninitializedArray<byte>(DiscordImageValidation.MaximumBytes);
            RandomNumberGenerator.Fill(bytes.AsSpan(0, 32));
            buffers.Add(bytes);
            _ = store.Stage(Binding(Guid.NewGuid()), Image(bytes));
        }

        Assert.Equal(DiscordEphemeralUploadStore.MaximumTotalBytes, store.TotalBytes);
        Assert.Throws<DiscordEphemeralUploadCapacityException>(() =>
            store.Stage(Binding(Guid.NewGuid()), Image([1, 2, 3, 4])));

        store.Dispose();

        Assert.All(buffers, value => Assert.Equal(-1, value.AsSpan().IndexOfAnyExcept((byte)0)));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.TotalBytes);
        Assert.Throws<ObjectDisposedException>(() =>
            store.Stage(Binding(Guid.NewGuid()), Image([1, 2, 3, 4])));
    }

    [Fact]
    public void RemovalIsActorIsolatedAndLeaseDisposalZeroesConsumedBytes()
    {
        using var store = new DiscordEphemeralUploadStore(TimeProvider.System);
        byte[] bytes = [1, 2, 3, 4];
        DiscordEphemeralUploadBinding binding = Binding(Guid.NewGuid());
        DiscordStagedUpload staged = store.Stage(binding, Image(bytes));

        Assert.False(store.Remove(staged.Handle, binding with { ActorUserId = Guid.NewGuid() }));
        DiscordEphemeralUploadLease lease = Assert.IsType<DiscordEphemeralUploadLease>(
            store.Consume(staged.Handle, binding));
        lease.Dispose();

        Assert.Equal([0, 0, 0, 0], bytes);
    }

    private static DiscordEphemeralUploadBinding Binding(Guid actor) => new(
        actor,
        Guid.NewGuid(),
        1,
        Guid.NewGuid(),
        "900000000000000001",
        Guid.NewGuid(),
        DiscordMessageMode.Embed,
        Spoiler: true,
        EmbedPlacement: true);

    private static DiscordValidatedImage Image(byte[] bytes) => new(
        bytes,
        "image-safe.png",
        "image/png",
        "Alt text",
        Spoiler: true,
        EmbedPlacement: true);
}
