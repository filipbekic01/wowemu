using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
builder.Services.AddSingleton<GameObjectTemplateStore>();
builder.Services.AddSingleton<ItemTemplateStore>();

// Two stores of the same shape, kept apart because a reference row points into the second one and
// a creature row must never resolve against itself.
builder.Services.AddKeyedSingleton("creature_loot", (_, _) => new LootStore("creature_loot_template"));
builder.Services.AddKeyedSingleton("reference_loot", (_, _) => new LootStore("reference_loot_template"));

builder.Services.AddSingleton<QuestStore>();

// Starter and ender are separate tables, and very often name different NPCs for the same quest.
builder.Services.AddKeyedSingleton(
    "quest_starters", (_, _) => new QuestRelationStore("creature_queststarter"));
builder.Services.AddKeyedSingleton(
    "quest_enders", (_, _) => new QuestRelationStore("creature_questender"));
builder.Services.AddSingleton<GameObjectSpawnStore>();
builder.Services.AddSingleton<PlayerXpStore>();
builder.Services.AddSingleton<GraveyardStore>();
builder.Services.AddInventoryDatabase();

// The DBC stores are read from disk once and never change, so they are built during registration
// rather than in the startup pass — a missing data directory should fail before anything else runs.
builder.Services.AddSingleton(_ => DbcStores.Load(
    Path.Combine(
        Path.IsPathRooted(startupOptions.DataDirectory)
            ? startupOptions.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, startupOptions.DataDirectory),
        "dbc")));

// Separate from DbcStores: Spell.dbc alone is 49,839 rows across 234 columns, and the four tables
// it indexes into are useless apart from it. Loading them together keeps a spell's cast time, range
// and duration resolvable in one place instead of at every call site.
// Keyed by race, class and gender rather than by id, which is the only way it is ever asked.
builder.Services.AddSingleton(_ => CharStartOutfitStore.Load(
    Path.Combine(
        Path.IsPathRooted(startupOptions.DataDirectory)
            ? startupOptions.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, startupOptions.DataDirectory),
        "dbc")));

builder.Services.AddSingleton(_ => SpellStores.Load(
    Path.Combine(
        Path.IsPathRooted(startupOptions.DataDirectory)
            ? startupOptions.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, startupOptions.DataDirectory),
        "dbc")));

builder.Services.AddSingleton(_ => new VmapManager(
    Path.IsPathRooted(startupOptions.DataDirectory)
        ? startupOptions.DataDirectory
        : Path.Combine(AppContext.BaseDirectory, startupOptions.DataDirectory)));

builder.Services.AddSingleton(_ => new TerrainManager(
    Path.IsPathRooted(startupOptions.DataDirectory)
        ? startupOptions.DataDirectory
        : Path.Combine(AppContext.BaseDirectory, startupOptions.DataDirectory)));

builder.Services.AddSingleton<WorldContent>();

// Grid object loading is registered as the interface the map layer asks for, so a map can be built
// without one — which is what the map tests do. A grid holds more than one kind of thing, so the
// loaders are composed rather than the map being taught about each.
builder.Services.AddSingleton<CreatureFactory>();
builder.Services.AddSingleton<CreatureGridLoader>();
builder.Services.AddSingleton<GameObjectGridLoader>();
builder.Services.AddSingleton<IGridObjectLoader>(services => new CompositeGridLoader(
[
    services.GetRequiredService<CreatureGridLoader>(),
    services.GetRequiredService<GameObjectGridLoader>(),
]));
builder.Services.AddSingleton(services => new MapManager(
    services.GetRequiredService<TerrainManager>(),
    services.GetRequiredService<IGridObjectLoader>(),
    new MapUpdater(startupOptions.MapUpdateThreads),
    services.GetRequiredService<ILogger<Map>>(),
    services.GetRequiredService<VmapManager>(),
    services.GetRequiredService<DbcStores>().FactionTemplates,
    services.GetRequiredService<PlayerXpStore>(),
    services.GetRequiredService<PlayerStatsStore>(),
    services.GetRequiredService<GraveyardStore>(),
    services.GetRequiredService<DbcStores>().WorldSafeLocs,
    services.GetRequiredService<SpellStores>(),
    services.GetRequiredService<ItemTemplateStore>(),
    services.GetRequiredKeyedService<LootStore>("creature_loot"),
    services.GetRequiredKeyedService<LootStore>("reference_loot"),
    services.GetRequiredService<ItemGuidGenerator>().Next,
    services.GetRequiredService<QuestStore>()));

// The tick has to be running before the listener accepts anyone: a session that queues a packet
// with nothing draining it would sit at the loading screen forever. Hosted services start in
// registration order, so this one goes first.
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<WorldLoop>();
builder.Services.AddHostedService(services => services.GetRequiredService<WorldLoop>());
builder.Services.AddHostedService<WorldServerHost>();

IHost host = builder.Build();

await WorldStartup.PrepareAsync(host.Services, startupOptions, CancellationToken.None);

await host.RunAsync();
