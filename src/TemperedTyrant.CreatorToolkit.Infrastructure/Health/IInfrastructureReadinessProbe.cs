namespace TemperedTyrant.CreatorToolkit.Infrastructure.Health;

public interface IInfrastructureReadinessProbe
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}
