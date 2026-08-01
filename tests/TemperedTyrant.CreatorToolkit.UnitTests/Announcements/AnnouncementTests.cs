using TemperedTyrant.CreatorToolkit.Core.Announcements;

namespace TemperedTyrant.CreatorToolkit.UnitTests.Announcements;

public sealed class AnnouncementTests
{
    private static readonly Guid ActorId = new("b31a6b8d-e6f8-4e3d-994c-5d876789d080");
    private static readonly DateTimeOffset InitialTime =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateTrimsTitlePreservesBodyAndStartsDraftAtRevisionOne()
    {
        string body = "  First paragraph.\n\nSecond paragraph.  ";

        AnnouncementCreationResult result = Announcement.Create(
            Guid.NewGuid(),
            "  Product update  ",
            body,
            ActorId,
            InitialTime.ToOffset(TimeSpan.FromHours(-4)));

        Assert.True(result.IsSuccess);
        Announcement announcement = Assert.IsType<Announcement>(result.Announcement);
        Assert.Equal("Product update", announcement.Title);
        Assert.Equal(body, announcement.Body);
        Assert.Equal(AnnouncementStatus.Draft, announcement.Status);
        Assert.Equal(InitialTime, announcement.CreatedAtUtc);
        Assert.Equal(InitialTime, announcement.UpdatedAtUtc);
        Assert.Equal(ActorId, announcement.CreatedByUserId);
        Assert.Equal(ActorId, announcement.UpdatedByUserId);
        Assert.Equal(1, announcement.Revision);
    }

    [Fact]
    public void ValidationUsesUnicodeScalarsAndRequiresNonWhitespaceContent()
    {
        string maximumTitle = string.Concat(
            Enumerable.Repeat("😀", Announcement.MaximumTitleScalarCount));
        string excessiveTitle = maximumTitle + "😀";
        string maximumBody = string.Concat(
            Enumerable.Repeat("𐐷", Announcement.MaximumBodyScalarCount));

        Assert.True(
            Announcement.Create(
                Guid.NewGuid(),
                maximumTitle,
                maximumBody,
                ActorId,
                InitialTime).IsSuccess);

        AnnouncementCreationResult invalid = Announcement.Create(
            Guid.NewGuid(),
            excessiveTitle,
            "\u2003\n\t",
            ActorId,
            InitialTime);

        Assert.False(invalid.IsSuccess);
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Field == nameof(Announcement.Title)
                && error.Message.Contains("200", StringComparison.Ordinal));
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Field == nameof(Announcement.Body)
                && error.Message == "Enter announcement content.");
    }

    [Fact]
    public void ValidTransitionsAdvanceRevisionActorAndControlledTimestampExactlyOnce()
    {
        Guid secondActor = Guid.NewGuid();
        Announcement announcement = CreateAnnouncement();

        AnnouncementDomainResult update = announcement.Update(
            "Updated",
            "Updated body",
            expectedRevision: 1,
            secondActor,
            InitialTime.AddMinutes(1));
        AnnouncementDomainResult archive = announcement.Archive(
            expectedRevision: 2,
            secondActor,
            InitialTime.AddMinutes(2));
        AnnouncementDomainResult restore = announcement.Restore(
            expectedRevision: 3,
            ActorId,
            InitialTime.AddMinutes(3));

        Assert.Equal(AnnouncementDomainStatus.Succeeded, update.Status);
        Assert.Equal(AnnouncementDomainStatus.Succeeded, archive.Status);
        Assert.Equal(AnnouncementDomainStatus.Succeeded, restore.Status);
        Assert.Equal(AnnouncementStatus.Draft, announcement.Status);
        Assert.Equal(4, announcement.Revision);
        Assert.Equal(ActorId, announcement.UpdatedByUserId);
        Assert.Equal(InitialTime.AddMinutes(3), announcement.UpdatedAtUtc);
    }

    [Fact]
    public void StaleAndInvalidTransitionsLeaveAggregateUnchanged()
    {
        Announcement announcement = CreateAnnouncement();
        Assert.Equal(
            AnnouncementDomainStatus.StaleRevision,
            announcement.Update(
                "Stale",
                "Stale",
                expectedRevision: 0,
                ActorId,
                InitialTime.AddMinutes(1)).Status);
        Assert.Equal(1, announcement.Revision);
        Assert.Equal("Original", announcement.Title);

        Assert.Equal(
            AnnouncementDomainStatus.Succeeded,
            announcement.Archive(1, ActorId, InitialTime.AddMinutes(2)).Status);
        Assert.Equal(
            AnnouncementDomainStatus.InvalidTransition,
            announcement.Update(
                "Blocked",
                "Blocked",
                expectedRevision: 2,
                ActorId,
                InitialTime.AddMinutes(3)).Status);
        Assert.Equal(
            AnnouncementDomainStatus.InvalidTransition,
            announcement.Archive(2, ActorId, InitialTime.AddMinutes(3)).Status);
        Assert.Equal(2, announcement.Revision);
        Assert.Equal("Original", announcement.Title);
    }

    private static Announcement CreateAnnouncement()
    {
        return Announcement.Create(
                Guid.NewGuid(),
                "Original",
                "Original body",
                ActorId,
                InitialTime)
            .Announcement!;
    }
}
