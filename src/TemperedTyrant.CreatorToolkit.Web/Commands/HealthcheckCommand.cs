using System.Net;

namespace TemperedTyrant.CreatorToolkit.Web.Commands;

public static class HealthcheckCommand
{
    public const string Name = "healthcheck";
    private static readonly Uri LiveEndpoint = new(
        "http://127.0.0.1:8080/health/live",
        UriKind.Absolute);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(1);

    public static async Task<int> RunAsync(
        CancellationToken cancellationToken = default)
    {
        using SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = ConnectionTimeout,
            UseCookies = false,
            UseProxy = false,
        };
        using HttpMessageInvoker client = new(handler);
        return await RunAsync(
            client,
            LiveEndpoint,
            OverallTimeout,
            cancellationToken);
    }

    internal static async Task<int> RunAsync(
        HttpMessageInvoker client,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                timeoutSource.Token);
            return response.StatusCode == HttpStatusCode.OK ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
