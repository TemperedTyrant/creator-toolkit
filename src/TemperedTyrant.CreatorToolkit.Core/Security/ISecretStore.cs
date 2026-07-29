namespace TemperedTyrant.CreatorToolkit.Core.Security;

public interface ISecretStore
{
    Task<SecretReference> CreateAsync(
        string purpose,
        string value,
        CancellationToken cancellationToken = default);

    Task ReplaceAsync(
        SecretReference secret,
        string value,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        SecretReference secret,
        CancellationToken cancellationToken = default);
}
