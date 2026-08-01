using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Web.Discord;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed class DiscordResultStoreTests
{
    [Fact]
    public void ConcurrentResultsRemainBoundedAndAnotherActorCannotConsumeThem()
    {
        var store = new DiscordPublicationResultStore(TimeProvider.System);
        Guid actor = Guid.NewGuid();
        Guid protectedSubmission = Guid.NewGuid();
        store.Put(actor, new DiscordPublicationResult(protectedSubmission, []));

        Assert.Null(store.Take(Guid.NewGuid(), protectedSubmission));
        Assert.NotNull(store.Take(actor, protectedSubmission));

        Parallel.For(
            0,
            500,
            _ =>
            {
                Guid submission = Guid.NewGuid();
                store.Put(actor, new DiscordPublicationResult(submission, []));
            });

        Assert.InRange(store.Count, 0, 100);
    }
}
