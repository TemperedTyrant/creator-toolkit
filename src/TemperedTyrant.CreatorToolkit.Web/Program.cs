using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using TemperedTyrant.CreatorToolkit.Infrastructure;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.Web.Authorization;
using TemperedTyrant.CreatorToolkit.Web.Commands;
using TemperedTyrant.CreatorToolkit.Web.Configuration;
using TemperedTyrant.CreatorToolkit.Web.Diagnostics;
using TemperedTyrant.CreatorToolkit.Web.ErrorHandling;
using TemperedTyrant.CreatorToolkit.Web.Health;
using TemperedTyrant.CreatorToolkit.Web.Hosting;
using TemperedTyrant.CreatorToolkit.Web.RateLimiting;
using TemperedTyrant.CreatorToolkit.Web.Security;

if (args.Length == 1
    && string.Equals(args[0], HealthcheckCommand.Name, StringComparison.Ordinal))
{
    return await HealthcheckCommand.RunAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("CREATOR_TOOLKIT_");

CreatorToolkitOptions toolkitOptions;
try
{
    toolkitOptions = CreatorToolkitOptionsValidator.GetValidated(
        builder.Configuration,
        builder.Environment.ContentRootPath);
    builder.Services.AddSingleton(toolkitOptions);
    builder.Services.AddCreatorToolkitInfrastructure(toolkitOptions.DataDirectory);
}
catch (Exception)
{
    await Console.Error.WriteLineAsync("Application startup failed.");
    return 1;
}

builder.Services.AddRazorPages();
builder.Services
    .AddAuthentication(
        options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(
    options =>
    {
        options.Cookie.Name = "creator-toolkit-auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.IsEssential = true;
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/AccessDenied";
        options.SlidingExpiration = true;
        options.EventsType = typeof(CreatorToolkitCookieEvents);
    });
builder.Services.AddScoped<CreatorToolkitCookieEvents>();
builder.Services.AddScoped<DebugStatusService>();
builder.Services.AddSingleton<ApplicationHostLockLifetime>();
builder.Services.AddSingleton<IHostedService>(
    provider => provider.GetRequiredService<ApplicationHostLockLifetime>());
builder.Services.AddSingleton<ApplicationLifecycleCoordinator>();
builder.Services.AddSingleton(ApplicationLifecycleOptions.Default);
builder.Services.AddSingleton(HealthReadinessOptions.Default);
builder.Services.AddSingleton<ApplicationReadinessService>();
builder.Services.AddHostedService<ApplicationLifecycleHostedService>();
builder.Services.Configure<HostOptions>(
    options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
builder.Services.Configure<SecurityStampValidatorOptions>(
    options => options.ValidationInterval = TimeSpan.Zero);
builder.Services.AddAuthorization(
    options =>
    {
        options.AddPolicy(
            AuthorizationPolicies.OwnerOnly,
            policy => policy.RequireRole(SystemRoles.Owner));
        options.AddPolicy(
            AuthorizationPolicies.Administration,
            policy => policy.RequireRole(SystemRoles.Owner, SystemRoles.Admin));
        options.AddPolicy(
            AuthorizationPolicies.ContentEditing,
            policy => policy.RequireRole(
                SystemRoles.Owner,
                SystemRoles.Admin,
                SystemRoles.Editor));
        options.AddPolicy(
            AuthorizationPolicies.ApplicationAccess,
            policy => policy.RequireRole(
                SystemRoles.Owner,
                SystemRoles.Admin,
                SystemRoles.Editor,
                SystemRoles.Viewer));
        options.AddPolicy(
            AuthorizationPolicies.ManageUsers,
            policy => policy.RequireRole(SystemRoles.Owner, SystemRoles.Admin));
        options.AddPolicy(
            AuthorizationPolicies.TransferOwnership,
            policy => policy.RequireRole(SystemRoles.Owner));
        options.FallbackPolicy = options
            .GetPolicy(AuthorizationPolicies.ApplicationAccess);
    });
builder.Services.AddRateLimiter(
    options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(
            RateLimitPolicies.Login,
            context => CreateSecurityFormRateLimitPartition(context, "login"));
        options.AddPolicy(
            RateLimitPolicies.Setup,
            context => CreateSecurityFormRateLimitPartition(context, "setup"));
        options.AddPolicy(
            RateLimitPolicies.Activation,
            context => CreateSecurityFormRateLimitPartition(context, "activation"));
        options.AddPolicy(
            RateLimitPolicies.OwnerRecovery,
            context => CreateSecurityFormRateLimitPartition(context, "owner-recovery"));
    });
builder.Services.Configure<ForwardedHeadersOptions>(
    options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in toolkitOptions.TrustedProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (var network in toolkitOptions.TrustedNetworks)
        {
            options.KnownIPNetworks.Add(network);
        }
    });

await using WebApplication app = builder.Build();

if (args.Length == 1
    && string.Equals(args[0], BootstrapOwnerCommand.Name, StringComparison.Ordinal))
{
    return await BootstrapOwnerCommand.RunAsync(
        app.Services,
        toolkitOptions,
        Console.Out,
        Console.Error);
}

if (args.Length >= 1
    && string.Equals(args[0], ResetOwnerCommand.Name, StringComparison.Ordinal))
{
    bool validArguments = args.Length == 1
        || (args.Length == 2
            && string.Equals(
                args[1],
                ResetOwnerCommand.NonInteractiveFlag,
                StringComparison.Ordinal));
    if (!validArguments)
    {
        await Console.Error.WriteLineAsync(
            $"Usage: creator-toolkit {ResetOwnerCommand.Name} [{ResetOwnerCommand.NonInteractiveFlag}]");
        return 1;
    }

    return await ResetOwnerCommand.RunAsync(
        app.Services,
        toolkitOptions,
        Console.In,
        Console.Out,
        Console.Error,
        nonInteractive: args.Length == 2);
}

try
{
    await app.Services
        .GetRequiredService<ApplicationHostLockLifetime>()
        .AcquireAsync(app.Lifetime.ApplicationStopping);

    await app.Services
        .GetRequiredService<PersistenceInitializer>()
        .InitializeAsync(app.Lifetime.ApplicationStopping);
}
catch (Exception)
{
    await Console.Error.WriteLineAsync("Application startup failed.");
    return 1;
}

if (toolkitOptions.TrustedProxies.Count > 0
    || toolkitOptions.TrustedNetworks.Count > 0)
{
    app.UseForwardedHeaders();
}

app.UseStaticFiles();
app.UseRouting();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<UnexpectedFailureMiddleware>();
app.UseMiddleware<SafeStatusCodeMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<AnonymousHealthEndpointMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapCreatorToolkitHealthEndpoints();
app.MapRazorPages();

await app.RunAsync();
return 0;

static RateLimitPartition<string> CreateSecurityFormRateLimitPartition(
    HttpContext context,
    string operation)
{
    return HttpMethods.IsPost(context.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(
            $"{operation}:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 5,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
            })
        : RateLimitPartition.GetNoLimiter($"{operation}:safe-method");
}

public partial class Program;
