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

// Three databases, three roles. The world server reads auth (session keys) but never migrates it —
// the logon server owns that schema, and two processes racing to migrate is a problem worth not
// having. It owns characters, and reads world as static content.
builder.Services.AddAuthDatabase(AuthDatabase.ResolveConnectionString(startupOptions.ConnectionString));
builder.Services.AddCharacterDatabase(
    CharacterDatabase.ResolveConnectionString(startupOptions.CharactersConnectionString));

builder.Services.AddSingleton<PlayerCreateInfoStore>();

builder.Services.AddHostedService<WorldServerHost>();

IHost host = builder.Build();

await WorldStartup.PrepareAsync(host.Services, startupOptions, CancellationToken.None);

await host.RunAsync();
