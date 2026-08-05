using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;
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
builder.Services.AddSingleton<PlayerStatsStore>();
builder.Services.AddSingleton<CreatureTemplateStore>();
builder.Services.AddSingleton<CreatureStatsStore>();
builder.Services.AddSingleton<CreatureSpawnStore>();

// The DBC stores are read from disk once and never change, so they are built during registration
// rather than in the startup pass — a missing data directory should fail before anything else runs.
builder.Services.AddSingleton(_ => DbcStores.Load(
    Path.Combine(
        Path.IsPathRooted(startupOptions.DataDirectory)
            ? startupOptions.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, startupOptions.DataDirectory),
        "dbc")));

builder.Services.AddSingleton(_ => new TerrainManager(
    Path.IsPathRooted(startupOptions.DataDirectory)
        ? startupOptions.DataDirectory
        : Path.Combine(AppContext.BaseDirectory, startupOptions.DataDirectory)));

builder.Services.AddSingleton<WorldContent>();

// Grid object loading is registered as the interface the map layer asks for, so a map can be built
// without one — which is what the map tests do.
builder.Services.AddSingleton<CreatureFactory>();
builder.Services.AddSingleton<IGridObjectLoader, CreatureGridLoader>();
builder.Services.AddSingleton(services => new MapManager(
    services.GetRequiredService<TerrainManager>(),
    services.GetRequiredService<IGridObjectLoader>()));

builder.Services.AddHostedService<WorldServerHost>();

IHost host = builder.Build();

await WorldStartup.PrepareAsync(host.Services, startupOptions, CancellationToken.None);

await host.RunAsync();
