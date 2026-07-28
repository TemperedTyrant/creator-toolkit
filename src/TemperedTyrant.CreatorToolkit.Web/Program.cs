using TemperedTyrant.CreatorToolkit.Infrastructure;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.Web.Configuration;
using TemperedTyrant.CreatorToolkit.Web.ErrorHandling;
using TemperedTyrant.CreatorToolkit.Web.Security;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("CREATOR_TOOLKIT_");

CreatorToolkitOptions toolkitOptions = CreatorToolkitOptionsValidator.GetValidated(
    builder.Configuration,
    builder.Environment.ContentRootPath);

builder.Services.AddSingleton(toolkitOptions);
builder.Services.AddCreatorToolkitInfrastructure(toolkitOptions.DataDirectory);
builder.Services.AddRazorPages();

WebApplication app = builder.Build();

await using ApplicationHostLease hostLease = await app.Services
    .GetRequiredService<ApplicationHostLock>()
    .AcquireAsync(app.Lifetime.ApplicationStopping);

await app.Services
    .GetRequiredService<PersistenceInitializer>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

app.UseRouting();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<UnexpectedFailureMiddleware>();
app.UseMiddleware<SafeStatusCodeMiddleware>();
app.MapRazorPages();

await app.RunAsync();

public partial class Program;
