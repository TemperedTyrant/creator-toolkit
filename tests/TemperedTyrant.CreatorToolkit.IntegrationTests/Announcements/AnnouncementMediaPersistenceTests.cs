using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.IntegrationTests.TestSupport;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Announcements;

public sealed class AnnouncementMediaPersistenceTests
{
    private static readonly Guid ActorId = new("ae116fcf-8a90-4dfe-8046-1b371a2ac0a0");
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x41, 0x52, 0x54, 0x49, 0x46, 0x41, 0x43, 0x54];

    [Fact]
    public async Task EncryptedMediaRoundTripsAcrossRestartAndPlaintextNeverEntersItsColumn()
    {
        using TestDataDirectory data = new();
        Guid announcementId = Guid.NewGuid();
        Guid mediaId;

        await using (ServiceProvider first = TestServices.Create(data.Path))
        {
            await TestServices.InitializeAsync(first);
            await using AsyncServiceScope scope = first.CreateAsyncScope();
            IAnnouncementService service = scope.ServiceProvider.GetRequiredService<IAnnouncementService>();
            AnnouncementOperationResult created = await service.CreateAsync(
                announcementId,
                "Internal title",
                "**Message source**",
                ActorId,
                [Upload(0, AnnouncementMediaPresentation.FeaturedImage)]);
            Assert.Equal(AnnouncementOperationStatus.Succeeded, created.Status);
            AnnouncementDetails details = Assert.IsType<AnnouncementDetails>(await service.GetAsync(announcementId));
            mediaId = Assert.Single(details.Media).Id;

            CreatorToolkitDbContext db = scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
            byte[] protectedContent = await db.AnnouncementMediaAssets
                .Where(value => value.Id == mediaId)
                .Select(value => value.ProtectedContent)
                .SingleAsync();
            Assert.False(protectedContent.AsSpan().IndexOf(Png) >= 0, "Media ciphertext contained the synthetic image marker.");
        }

        await using ServiceProvider second = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(second);
        await using AsyncServiceScope verification = second.CreateAsyncScope();
        AnnouncementMediaContent content = Assert.IsType<AnnouncementMediaContent>(
            await verification.ServiceProvider.GetRequiredService<IAnnouncementService>()
                .GetMediaContentAsync(announcementId, mediaId));
        try
        {
            Assert.Equal(Png, content.Bytes);
            Assert.Equal("image/png", content.ContentType);
            Assert.DoesNotContain(".png.png", content.GeneratedFileName, StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content.Bytes);
        }
    }

    [Fact]
    public async Task MediaEditIsRevisionBoundTransactionalOrderedAndCascadeDeleted()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid announcementId = Guid.NewGuid();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IAnnouncementService service = scope.ServiceProvider.GetRequiredService<IAnnouncementService>();
        Assert.Equal(
            AnnouncementOperationStatus.Succeeded,
            (await service.CreateAsync(
                announcementId,
                "Internal",
                "Message",
                ActorId,
                [Upload(0), Upload(1, spoiler: true)])).Status);
        AnnouncementDetails original = Assert.IsType<AnnouncementDetails>(await service.GetAsync(announcementId));

        AnnouncementMediaChangeSet stale = new(
            original.Media.Select(value => new AnnouncementMediaEdit(
                value.Id,
                value.Revision + 1,
                value.SortOrder,
                value.AltText,
                value.IsSpoiler,
                value.Presentation,
                Remove: value.SortOrder == 0)).ToArray(),
            []);
        Assert.Equal(
            AnnouncementOperationStatus.StaleRevision,
            (await service.UpdateAsync(
                announcementId,
                "Unsafe stale title",
                "Unsafe stale content",
                original.Revision,
                ActorId,
                stale)).Status);
        Assert.Equal(2, (await service.GetAsync(announcementId))!.Media.Count);

        AnnouncementMediaChangeSet valid = new(
            original.Media.Select(value => new AnnouncementMediaEdit(
                value.Id,
                value.Revision,
                1 - value.SortOrder,
                value.SortOrder == 0 ? "Updated alt" : value.AltText,
                value.IsSpoiler,
                value.SortOrder == 0
                    ? AnnouncementMediaPresentation.FeaturedImage
                    : AnnouncementMediaPresentation.Attachment,
                Remove: false)).ToArray(),
            []);
        AnnouncementOperationResult updated = await service.UpdateAsync(
            announcementId,
            "Internal",
            "Message",
            original.Revision,
            ActorId,
            valid);
        Assert.Equal(AnnouncementOperationStatus.Succeeded, updated.Status);
        Assert.Equal(original.Revision + 1, updated.Revision);
        AnnouncementDetails reordered = Assert.IsType<AnnouncementDetails>(await service.GetAsync(announcementId));
        Assert.Equal([0, 1], reordered.Media.Select(value => value.SortOrder));
        Assert.Single(reordered.Media, value => value.Presentation == AnnouncementMediaPresentation.FeaturedImage);

        Assert.Equal(
            AnnouncementOperationStatus.Succeeded,
            (await service.DeleteAsync(announcementId, reordered.Revision, ActorId)).Status);
        CreatorToolkitDbContext db = scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Empty(await db.AnnouncementMediaAssets.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task CorruptedCiphertextAndAnotherMediaPurposeFailClosed()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);
        Guid announcementId = Guid.NewGuid();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IAnnouncementService service = scope.ServiceProvider.GetRequiredService<IAnnouncementService>();
        await service.CreateAsync(announcementId, "Internal", "Message", ActorId, [Upload(0)]);
        CreatorToolkitDbContext db = scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        AnnouncementMediaAsset media = await db.AnnouncementMediaAssets.SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AnnouncementMediaAssets SET ProtectedContent = {new byte[] { 0x01, 0x02, 0x03 }} WHERE Id = {media.Id}");
        db.ChangeTracker.Clear();
        Assert.Null(await service.GetMediaContentAsync(announcementId, media.Id));

        AnnouncementMediaProtector protector = scope.ServiceProvider.GetRequiredService<AnnouncementMediaProtector>();
        Guid firstId = Guid.NewGuid();
        byte[] ciphertext = protector.Protect(announcementId, firstId, Png);
        var wrongPurpose = AnnouncementMediaAsset.Create(
            Guid.NewGuid(),
            announcementId,
            0,
            ciphertext,
            "image/png",
            Png.Length,
            SHA256.HashData(Png),
            "announcement-safe.png",
            null,
            false,
            AnnouncementMediaPresentation.Attachment,
            DateTimeOffset.UtcNow);
        Assert.Throws<AnnouncementMediaUnavailableException>(() => protector.Unprotect(wrongPurpose));
    }

    [Fact]
    public async Task InvalidSignatureCountSizeAndFeaturedCombinationsCommitNothing()
    {
        using TestDataDirectory data = new();
        await using ServiceProvider provider = TestServices.Create(data.Path);
        await TestServices.InitializeAsync(provider);

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IAnnouncementService service = scope.ServiceProvider.GetRequiredService<IAnnouncementService>();
        Assert.Equal(
            AnnouncementOperationStatus.ValidationFailed,
            (await service.CreateAsync(
                Guid.NewGuid(),
                "Internal",
                "Message",
                ActorId,
                [new AnnouncementMediaUpload([1, 2, 3], "bad.png", "image/png", null, false, AnnouncementMediaPresentation.Attachment, 0)])).Status);
        Assert.Equal(
            AnnouncementOperationStatus.ValidationFailed,
            (await service.CreateAsync(
                Guid.NewGuid(),
                "Internal",
                "Message",
                ActorId,
                Enumerable.Range(0, 5).Select(value => Upload(value)).ToArray())).Status);
        Assert.Equal(
            AnnouncementOperationStatus.ValidationFailed,
            (await service.CreateAsync(
                Guid.NewGuid(),
                "Internal",
                "Message",
                ActorId,
                [Upload(0, AnnouncementMediaPresentation.FeaturedImage), Upload(1, AnnouncementMediaPresentation.FeaturedImage)])).Status);

        int largeLength = (AnnouncementMediaAsset.MaximumCombinedBytes / 2) + 1;
        byte[] first = new byte[largeLength];
        byte[] second = new byte[largeLength];
        Png.CopyTo(first, 0);
        Png.CopyTo(second, 0);
        Assert.Equal(
            AnnouncementOperationStatus.ValidationFailed,
            (await service.CreateAsync(
                Guid.NewGuid(),
                "Internal",
                "Message",
                ActorId,
                [
                    new AnnouncementMediaUpload(first, "first.png", "image/png", null, false, AnnouncementMediaPresentation.Attachment, 0),
                    new AnnouncementMediaUpload(second, "second.png", "image/png", null, false, AnnouncementMediaPresentation.Attachment, 1),
                ])).Status);
        CryptographicOperations.ZeroMemory(first);
        CryptographicOperations.ZeroMemory(second);

        CreatorToolkitDbContext db = scope.ServiceProvider.GetRequiredService<CreatorToolkitDbContext>();
        Assert.Empty(await db.Announcements.ToArrayAsync());
        Assert.Empty(await db.AnnouncementMediaAssets.ToArrayAsync());
        Assert.Empty(await db.AuditRecords.ToArrayAsync());
    }

    private static AnnouncementMediaUpload Upload(
        int order,
        AnnouncementMediaPresentation presentation = AnnouncementMediaPresentation.Attachment,
        bool spoiler = false) =>
        new(Png.ToArray(), $"synthetic-{order}.png", "image/png", $"Image {order}", spoiler, presentation, order);
}
