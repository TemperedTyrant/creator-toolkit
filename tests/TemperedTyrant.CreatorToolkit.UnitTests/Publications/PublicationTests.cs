using TemperedTyrant.CreatorToolkit.Core.Publications;

namespace TemperedTyrant.CreatorToolkit.UnitTests.Publications;

public sealed class PublicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AggregateStatusReflectsActiveAndTerminalDeliveryCombinations()
    {
        PublicationDelivery first = Delivery("100000000000000001");
        PublicationDelivery second = Delivery("100000000000000002");
        Assert.Equal(PublicationStatus.Queued, Publication.CalculateStatus([first, second], false));

        Assert.True(first.TryClaim("lease-a", Now, TimeSpan.FromSeconds(45)));
        Assert.Equal(PublicationStatus.Processing, Publication.CalculateStatus([first, second], false));
        Assert.True(first.Complete("lease-a", first.Revision, "success", "200000000000000001", Now));
        Assert.True(second.TryClaim("lease-b", Now, TimeSpan.FromSeconds(45)));
        Assert.True(second.FailPermanent("lease-b", second.Revision, "missing-permission", Now));

        Assert.Equal(
            PublicationStatus.PartiallySucceeded,
            Publication.CalculateStatus([first, second], false));
    }

    [Fact]
    public void CancellationNeverChangesSuccessfulDelivery()
    {
        PublicationDelivery succeeded = Delivery("100000000000000001");
        PublicationDelivery pending = Delivery("100000000000000002");
        Assert.True(succeeded.TryClaim("lease-a", Now, TimeSpan.FromSeconds(45)));
        Assert.True(succeeded.Complete("lease-a", succeeded.Revision, "success", null, Now));

        Assert.False(succeeded.CancelPending(Now));
        Assert.True(pending.CancelPending(Now));
        Assert.Equal(PublicationDeliveryStatus.Succeeded, succeeded.Status);
        Assert.Equal(
            PublicationStatus.PartiallySucceeded,
            Publication.CalculateStatus([succeeded, pending], true));
    }

    [Fact]
    public void ActiveLeaseCannotBeStolenAndExpiredLeaseIsRecoverable()
    {
        PublicationDelivery delivery = Delivery("100000000000000001");
        Assert.True(delivery.TryClaim("lease-a", Now, TimeSpan.FromSeconds(45)));
        Assert.False(delivery.TryClaim("lease-b", Now.AddSeconds(44), TimeSpan.FromSeconds(45)));
        Assert.True(delivery.TryClaim("lease-b", Now.AddSeconds(45), TimeSpan.FromSeconds(45)));
        Assert.Equal(2, delivery.AttemptCount);
    }

    [Fact]
    public void StaleOrWrongLeaseCompletionCannotOverwriteCurrentState()
    {
        PublicationDelivery delivery = Delivery("100000000000000001");
        Assert.True(delivery.TryClaim("lease-a", Now, TimeSpan.FromSeconds(45)));
        long firstRevision = delivery.Revision;
        Assert.True(delivery.TryClaim("lease-b", Now.AddSeconds(45), TimeSpan.FromSeconds(45)));

        Assert.False(delivery.Complete("lease-a", firstRevision, "success", null, Now.AddSeconds(46)));
        Assert.False(delivery.Complete("lease-a", delivery.Revision, "success", null, Now.AddSeconds(46)));
        Assert.Equal(PublicationDeliveryStatus.Leased, delivery.Status);
        Assert.True(delivery.Complete("lease-b", delivery.Revision, "success", null, Now.AddSeconds(46)));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 600)]
    public void RetryPolicyUsesDeterministicBoundedDelays(int attempt, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            PublicationRetryPolicy.DelayAfterAttempt(attempt));
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            PublicationRetryPolicy.DelayAfterAttempt(attempt, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void CancellationIsRevisionBoundAndCannotBeRequestedTwice()
    {
        Publication publication = Publication.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            Now);
        Assert.Equal(
            PublicationMutationResult.StaleRevision,
            publication.RequestCancellation(2, Now));
        Assert.Equal(
            PublicationMutationResult.Succeeded,
            publication.RequestCancellation(1, Now));
        Assert.Equal(PublicationStatus.Cancelling, publication.Status);
        Assert.Equal(
            PublicationMutationResult.InvalidTransition,
            publication.RequestCancellation(publication.Revision, Now));
    }

    private static PublicationDelivery Delivery(string channelId) => PublicationDelivery.Create(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        channelId,
        "Server",
        "Channel",
        "stable-nonce",
        Now);
}
