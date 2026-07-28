namespace TemperedTyrant.CreatorToolkit.Web.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    internal const string ApplicationContentSecurityPolicy =
        "default-src 'self'; object-src 'none'; base-uri 'none'; "
        + "frame-ancestors 'none'; form-action 'self'";

    internal const string SensitiveContentSecurityPolicy =
        "default-src 'none'; object-src 'none'; base-uri 'none'; "
        + "frame-ancestors 'none'; form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool isSensitiveEndpoint = context
            .GetEndpoint()?
            .Metadata
            .GetMetadata<SensitiveSecurityHeaderProfileAttribute>() is not null;

        context.Response.Headers.ContentSecurityPolicy =
            isSensitiveEndpoint
                ? SensitiveContentSecurityPolicy
                : ApplicationContentSecurityPolicy;
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.Append(
            "Permissions-Policy",
            "camera=(), microphone=(), geolocation=()");

        if (isSensitiveEndpoint)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
        }

        return next(context);
    }
}
