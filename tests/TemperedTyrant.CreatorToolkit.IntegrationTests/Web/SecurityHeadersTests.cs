using Microsoft.AspNetCore.Http;
using TemperedTyrant.CreatorToolkit.Web.Security;

namespace TemperedTyrant.CreatorToolkit.IntegrationTests.Web;

public sealed class SecurityHeadersTests
{
    [Fact]
    public async Task ApplicationProfileSetsCentralHeadersWithoutGlobalNoStoreOrHsts()
    {
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask);
        DefaultHttpContext context = new();

        await middleware.InvokeAsync(context);

        Assert.Equal(
            "default-src 'self'; object-src 'none'; base-uri 'none'; "
            + "frame-ancestors 'none'; form-action 'self'",
            context.Response.Headers.ContentSecurityPolicy);
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions);
        Assert.Equal(
            "camera=(), microphone=(), geolocation=()",
            context.Response.Headers["Permissions-Policy"]);
        Assert.False(context.Response.Headers.ContainsKey("Cache-Control"));
        Assert.False(context.Response.Headers.ContainsKey("Pragma"));
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task EndpointCanSelectStricterProfileWithoutChangingGlobalProfile()
    {
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask);
        DefaultHttpContext sensitiveContext = new();
        sensitiveContext.SetEndpoint(
            new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(
                    new SensitiveSecurityHeaderProfileAttribute()),
                "sensitive"));

        await middleware.InvokeAsync(sensitiveContext);

        Assert.Equal(
            "default-src 'none'; style-src 'self'; object-src 'none'; base-uri 'none'; "
            + "frame-ancestors 'none'; form-action 'self'",
            sensitiveContext.Response.Headers.ContentSecurityPolicy);
        Assert.Equal("no-store", sensitiveContext.Response.Headers.CacheControl);
        Assert.Equal("no-cache", sensitiveContext.Response.Headers.Pragma);

        DefaultHttpContext applicationContext = new();
        await middleware.InvokeAsync(applicationContext);
        Assert.Equal(
            "default-src 'self'; object-src 'none'; base-uri 'none'; "
            + "frame-ancestors 'none'; form-action 'self'",
            applicationContext.Response.Headers.ContentSecurityPolicy);
        Assert.False(applicationContext.Response.Headers.ContainsKey("Cache-Control"));
    }

    [Fact]
    public async Task HealthProfileIsMinimalAndNeverCacheable()
    {
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask);
        DefaultHttpContext context = new();
        context.SetEndpoint(
            new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(
                    new HealthSecurityHeaderProfileAttribute()),
                "health"));

        await middleware.InvokeAsync(context);

        Assert.Equal(
            "default-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'",
            context.Response.Headers.ContentSecurityPolicy);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("no-cache", context.Response.Headers.Pragma);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
    }
}
