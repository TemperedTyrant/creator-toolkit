namespace TemperedTyrant.CreatorToolkit.Web.Health;

public sealed class AnonymousHealthEndpointMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Endpoint? endpoint = context.GetEndpoint();
        if (endpoint?.RequestDelegate is not null
            && endpoint.Metadata.GetMetadata<AnonymousHealthEndpointAttribute>() is not null)
        {
            return endpoint.RequestDelegate(context);
        }

        return next(context);
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class AnonymousHealthEndpointAttribute : Attribute;
