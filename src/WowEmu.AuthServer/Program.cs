using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WowEmu.AuthServer;
using WowEmu.Data.Db;

// Content root follows the binary, not the working directory: appsettings.json is copied next to
// the executable, and the VS Code launch config runs it with cwd set to the repository root.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services
    .AddOptions<AuthServerOptions>()
    .Bind(builder.Configuration.GetSection(AuthServerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Read once, eagerly: the database has to be registered before the host is built, which is before
// the options system is available.
AuthServerOptions startupOptions =
    builder.Configuration.GetSection(AuthServerOptions.SectionName).Get<AuthServerOptions>() ?? new AuthServerOptions();

builder.Services.AddAuthDatabase(AuthDatabase.ResolveConnectionString(startupOptions.ConnectionString));

builder.Services.AddSingleton<RealmList>();

// Order matters: the refresher loads the realm list during startup, before the listener accepts.
builder.Services.AddHostedService<RealmListRefresher>();
builder.Services.AddHostedService<AuthServerHost>();

IHost host = builder.Build();

await DatabaseStartup.PrepareAsync(host.Services, CancellationToken.None);

await host.RunAsync();
