namespace TemperedTyrant.CreatorToolkit.Web.ErrorHandling;

public sealed class SafeStatusCodeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await next(context);

        if (context.Response.HasStarted
            || context.Response.ContentLength is not null
            || !string.IsNullOrEmpty(context.Response.ContentType)
            || !SafeHttpFailureResponses.IsExpected(context.Response.StatusCode))
        {
            return;
        }

        await SafeHttpFailureResponses.WriteAsync(context, context.Response.StatusCode);
    }
}
