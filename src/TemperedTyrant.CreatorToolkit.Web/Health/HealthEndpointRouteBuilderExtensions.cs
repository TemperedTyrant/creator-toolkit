using Microsoft.AspNetCore.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.Web.Health;

public static class HealthEndpointRouteBuilderExtensions
{
    private const string LiveResponse = "{\"status\":\"live\"}";
    private const string ReadyResponse = "{\"status\":\"ready\"}";
    private const string NotReadyResponse = "{\"status\":\"not_ready\"}";
    private const string JsonContentType = "application/json; charset=utf-8";
    private static readonly string[] UnsupportedHealthMethods =
    [
        HttpMethods.Connect,
        HttpMethods.Delete,
        HttpMethods.Head,
        HttpMethods.Options,
        HttpMethods.Patch,
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Trace,
    ];

    public static IEndpointRouteBuilder MapCreatorToolkitHealthEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(
                "/health/live",
                static (HttpContext context) => WriteResponseAsync(
                    context,
                    StatusCodes.Status200OK,
                    LiveResponse))
            .AllowAnonymous()
            .WithMetadata(new AnonymousHealthEndpointAttribute())
            .WithMetadata(new HealthSecurityHeaderProfileAttribute());

        endpoints
            .MapGet(
                "/health/ready",
                static async (
                    HttpContext context,
                    ApplicationReadinessService readinessService) =>
                {
                    bool isReady = await readinessService.IsReadyAsync(
                        context.RequestAborted);
                    await WriteResponseAsync(
                        context,
                        isReady
                            ? StatusCodes.Status200OK
                            : StatusCodes.Status503ServiceUnavailable,
                        isReady ? ReadyResponse : NotReadyResponse);
                })
            .AllowAnonymous()
            .WithMetadata(new AnonymousHealthEndpointAttribute())
            .WithMetadata(new HealthSecurityHeaderProfileAttribute());

        MapUnsupportedMethods(endpoints, "/health/live");
        MapUnsupportedMethods(endpoints, "/health/ready");

        return endpoints;
    }

    private static void MapUnsupportedMethods(
        IEndpointRouteBuilder endpoints,
        string path)
    {
        endpoints
            .MapMethods(
                path,
                UnsupportedHealthMethods,
                static (HttpContext context) =>
                {
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    context.Response.Headers.Allow = HttpMethods.Get;
                    return Task.CompletedTask;
                })
            .AllowAnonymous()
            .WithMetadata(new AnonymousHealthEndpointAttribute())
            .WithMetadata(new HealthSecurityHeaderProfileAttribute());
    }

    private static Task WriteResponseAsync(
        HttpContext context,
        int statusCode,
        string response)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = JsonContentType;
        return context.Response.WriteAsync(response, context.RequestAborted);
    }
}
