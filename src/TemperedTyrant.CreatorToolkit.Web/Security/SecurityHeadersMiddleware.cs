namespace TemperedTyrant.CreatorToolkit.Web.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    internal const string ApplicationContentSecurityPolicy =
        "default-src 'self'; object-src 'none'; base-uri 'none'; "
        + "frame-ancestors 'none'; form-action 'self'";

    internal const string SensitiveContentSecurityPolicy =
        "default-src 'none'; style-src 'self'; object-src 'none'; base-uri 'none'; "
        + "frame-ancestors 'none'; form-action 'self'";

    internal const string SetupContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; object-src 'none'; base-uri 'none'; "
        + "frame-ancestors 'none'; form-action 'self'";

    internal const string HealthContentSecurityPolicy =
        "default-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(
            static state =>
            {
                ApplyHeaders((HttpContext)state);
                return Task.CompletedTask;
            },
            context);
        ApplyHeaders(context);
        return next(context);
    }

    private static void ApplyHeaders(HttpContext context)
    {
        bool isSensitiveEndpoint = context
            .GetEndpoint()?
            .Metadata
            .GetMetadata<SensitiveSecurityHeaderProfileAttribute>() is not null;
        bool isSetupEndpoint = context
            .GetEndpoint()?
            .Metadata
            .GetMetadata<SetupSecurityHeaderProfileAttribute>() is not null;
        bool isCapabilityEndpoint = context
            .GetEndpoint()?
            .Metadata
            .GetMetadata<CapabilitySecurityHeaderProfileAttribute>() is not null;
        bool isHealthEndpoint = context
            .GetEndpoint()?
            .Metadata
            .GetMetadata<HealthSecurityHeaderProfileAttribute>() is not null;

        context.Response.Headers.ContentSecurityPolicy =
            isHealthEndpoint
                ? HealthContentSecurityPolicy
                : isSetupEndpoint || isCapabilityEndpoint
                ? SetupContentSecurityPolicy
                : isSensitiveEndpoint
                ? SensitiveContentSecurityPolicy
                : ApplicationContentSecurityPolicy;
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=()";

        if (isSensitiveEndpoint
            || isSetupEndpoint
            || isCapabilityEndpoint
            || isHealthEndpoint)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
        }
    }
}
