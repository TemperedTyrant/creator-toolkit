namespace TemperedTyrant.CreatorToolkit.Web.ErrorHandling;

internal static class SafeHttpFailureResponses
{
    private static readonly Dictionary<int, string> Responses =
        new()
        {
            [StatusCodes.Status400BadRequest] = "The request could not be processed.",
            [StatusCodes.Status401Unauthorized] = "Authentication is required.",
            [StatusCodes.Status403Forbidden] = "Access is denied.",
            [StatusCodes.Status404NotFound] = "The requested resource was not found.",
            [StatusCodes.Status409Conflict] = "The request conflicts with the current state.",
            [StatusCodes.Status429TooManyRequests] = "Too many requests. Try again later.",
        };

    internal static bool IsExpected(int statusCode)
    {
        return Responses.ContainsKey(statusCode);
    }

    internal static async Task WriteAsync(HttpContext context, int statusCode)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(Responses[statusCode], context.RequestAborted);
    }
}
