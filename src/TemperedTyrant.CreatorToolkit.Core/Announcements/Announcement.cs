using System.Text;

namespace TemperedTyrant.CreatorToolkit.Core.Announcements;

public sealed class Announcement
{
    public const int MaximumTitleScalarCount = 200;
    public const int MaximumBodyScalarCount = 10_000;

    private Announcement()
    {
    }

    public Guid Id { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public AnnouncementStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public Guid UpdatedByUserId { get; private set; }

    public long Revision { get; private set; }

    public static AnnouncementCreationResult Create(
        Guid id,
        string? title,
        string? body,
        Guid actorUserId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(actorUserId, Guid.Empty);

        AnnouncementContentValidation validation = ValidateContent(title, body);
        if (validation.Errors.Count > 0)
        {
            return AnnouncementCreationResult.ValidationFailed(validation.Errors);
        }

        DateTimeOffset utc = occurredAtUtc.ToUniversalTime();
        return AnnouncementCreationResult.Succeeded(
            new Announcement
            {
                Id = id,
                Title = validation.Title,
                Body = validation.Body,
                Status = AnnouncementStatus.Draft,
                CreatedAtUtc = utc,
                UpdatedAtUtc = utc,
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId,
                Revision = 1,
            });
    }

    public AnnouncementDomainResult Update(
        string? title,
        string? body,
        long expectedRevision,
        Guid actorUserId,
        DateTimeOffset occurredAtUtc)
    {
        if (Revision != expectedRevision)
        {
            return AnnouncementDomainResult.StaleRevision();
        }

        if (Status != AnnouncementStatus.Draft)
        {
            return AnnouncementDomainResult.InvalidTransition();
        }

        AnnouncementContentValidation validation = ValidateContent(title, body);
        if (validation.Errors.Count > 0)
        {
            return AnnouncementDomainResult.ValidationFailed(validation.Errors);
        }

        Title = validation.Title;
        Body = validation.Body;
        RecordMutation(actorUserId, occurredAtUtc);
        return AnnouncementDomainResult.Succeeded();
    }

    public AnnouncementDomainResult Archive(
        long expectedRevision,
        Guid actorUserId,
        DateTimeOffset occurredAtUtc)
    {
        if (Revision != expectedRevision)
        {
            return AnnouncementDomainResult.StaleRevision();
        }

        if (Status != AnnouncementStatus.Draft)
        {
            return AnnouncementDomainResult.InvalidTransition();
        }

        Status = AnnouncementStatus.Archived;
        RecordMutation(actorUserId, occurredAtUtc);
        return AnnouncementDomainResult.Succeeded();
    }

    public AnnouncementDomainResult Restore(
        long expectedRevision,
        Guid actorUserId,
        DateTimeOffset occurredAtUtc)
    {
        if (Revision != expectedRevision)
        {
            return AnnouncementDomainResult.StaleRevision();
        }

        if (Status != AnnouncementStatus.Archived)
        {
            return AnnouncementDomainResult.InvalidTransition();
        }

        Status = AnnouncementStatus.Draft;
        RecordMutation(actorUserId, occurredAtUtc);
        return AnnouncementDomainResult.Succeeded();
    }

    private static AnnouncementContentValidation ValidateContent(
        string? title,
        string? body)
    {
        string normalizedTitle = title?.Trim() ?? string.Empty;
        string preservedBody = body ?? string.Empty;
        List<AnnouncementValidationError> errors = [];

        ValidateRequiredPlainText(
            normalizedTitle,
            nameof(Title),
            MaximumTitleScalarCount,
            "Enter a title.",
            $"The title must be {MaximumTitleScalarCount} Unicode characters or fewer.",
            errors);
        ValidateRequiredPlainText(
            preservedBody,
            nameof(Body),
            MaximumBodyScalarCount,
            "Enter announcement content.",
            $"The announcement content must be {MaximumBodyScalarCount:N0} Unicode characters or fewer.",
            errors);

        return new AnnouncementContentValidation(normalizedTitle, preservedBody, errors);
    }

    private static void ValidateRequiredPlainText(
        string value,
        string field,
        int maximumScalarCount,
        string requiredMessage,
        string lengthMessage,
        List<AnnouncementValidationError> errors)
    {
        bool hasNonWhitespace = false;
        int scalarCount = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            scalarCount++;
            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
        }

        if (!hasNonWhitespace)
        {
            errors.Add(new AnnouncementValidationError(field, requiredMessage));
        }

        if (scalarCount > maximumScalarCount)
        {
            errors.Add(new AnnouncementValidationError(field, lengthMessage));
        }
    }

    private void RecordMutation(Guid actorUserId, DateTimeOffset occurredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actorUserId, Guid.Empty);
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = occurredAtUtc.ToUniversalTime();
        Revision = checked(Revision + 1);
    }

    private sealed record AnnouncementContentValidation(
        string Title,
        string Body,
        IReadOnlyList<AnnouncementValidationError> Errors);
}

public enum AnnouncementStatus
{
    Draft = 1,
    Archived = 2,
}

public sealed record AnnouncementValidationError(string Field, string Message);

public sealed record AnnouncementCreationResult(
    Announcement? Announcement,
    IReadOnlyList<AnnouncementValidationError> ValidationErrors)
{
    public bool IsSuccess => Announcement is not null;

    public static AnnouncementCreationResult Succeeded(Announcement announcement) =>
        new(announcement, []);

    public static AnnouncementCreationResult ValidationFailed(
        IReadOnlyList<AnnouncementValidationError> errors) =>
        new(null, errors);
}

public sealed record AnnouncementDomainResult(
    AnnouncementDomainStatus Status,
    IReadOnlyList<AnnouncementValidationError> ValidationErrors)
{
    public static AnnouncementDomainResult Succeeded() =>
        new(AnnouncementDomainStatus.Succeeded, []);

    public static AnnouncementDomainResult StaleRevision() =>
        new(AnnouncementDomainStatus.StaleRevision, []);

    public static AnnouncementDomainResult InvalidTransition() =>
        new(AnnouncementDomainStatus.InvalidTransition, []);

    public static AnnouncementDomainResult ValidationFailed(
        IReadOnlyList<AnnouncementValidationError> errors) =>
        new(AnnouncementDomainStatus.ValidationFailed, errors);
}

public enum AnnouncementDomainStatus
{
    Succeeded = 1,
    StaleRevision = 2,
    InvalidTransition = 3,
    ValidationFailed = 4,
}
