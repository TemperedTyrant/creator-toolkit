using TemperedTyrant.CreatorToolkit.Infrastructure;
using TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;
using TemperedTyrant.CreatorToolkit.Infrastructure.ProcessCoordination;
using TemperedTyrant.CreatorToolkit.Web.Configuration;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("CREATOR_TOOLKIT_");

CreatorToolkitOptions toolkitOptions = CreatorToolkitOptionsValidator.GetValidated(
    builder.Configuration,
    builder.Environment.ContentRootPath);

builder.Services.AddSingleton(toolkitOptions);
builder.Services.AddCreatorToolkitInfrastructure(toolkitOptions.DataDirectory);

WebApplication app = builder.Build();

await using ApplicationHostLease hostLease = await app.Services
    .GetRequiredService<ApplicationHostLock>()
    .AcquireAsync(app.Lifetime.ApplicationStopping);

await app.Services
    .GetRequiredService<PersistenceInitializer>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

await app.RunAsync();

public partial class Program;
