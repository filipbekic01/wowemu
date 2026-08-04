using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WowEmu.Data.Db;
using WowEmu.WorldServer;

// Content root follows the binary, not the working directory — same reason as the logon server.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services
    .AddOptions<WorldServerOptions>()
    .Bind(builder.Configuration.GetSection(WorldServerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

WorldServerOptions startupOptions =
    builder.Configuration.GetSection(WorldServerOptions.SectionName).Get<WorldServerOptions>() ?? new WorldServerOptions();

// The world server reads the auth database but never migrates it: the logon server owns that
// schema. Two processes racing to apply migrations is a problem worth not having.
builder.Services.AddAuthDatabase(AuthDatabase.ResolveConnectionString(startupOptions.ConnectionString));

builder.Services.AddHostedService<WorldServerHost>();

IHost host = builder.Build();
await host.RunAsync();
