using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game.Maps;

namespace WowEmu.WorldServer;

/// <summary>
/// Brings the characters schema up to date and loads static world content, before anything listens.
/// </summary>
/// <remarks>
/// Static data is loaded eagerly and the host fails if it cannot be: a world server that accepts
/// connections and only then discovers it has no start positions would reject every character
/// creation with an error that looks like a client problem.
/// </remarks>
internal static class WorldStartup
{
    public static async Task PrepareAsync(
        IServiceProvider services,
        WorldServerOptions options,
        CancellationToken cancellationToken)
    {
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        StartupReport report = new();

        if (options.ApplyMigrationsOnStartup)
        {
            IDbContextFactory<CharactersDbContext> contextFactory =
                services.GetRequiredService<IDbContextFactory<CharactersDbContext>>();

            await using CharactersDbContext context =
                await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> pending =
                [.. await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)];

            if (pending.Count > 0)
            {
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                Log.MigrationsApplied(logger, pending.Count);
            }
        }

        // After the migrations, before anything can create an item. Reissuing a guid the database
        // already holds makes two items share an identity, and one overwrites the other on the
        // next save — silently, and only for whoever logged out second.
        ItemGuidGenerator itemGuids = services.GetRequiredService<ItemGuidGenerator>();
        IInventoryRepository inventory = services.GetRequiredService<IInventoryRepository>();

        itemGuids.SeedFrom(await inventory.HighestItemIdAsync(cancellationToken).ConfigureAwait(false));
        Log.ItemGuidsSeeded(logger, itemGuids.Last);

        string worldConnection = PlayerCreateInfoStore.ResolveConnectionString(options.WorldConnectionString);

        PlayerCreateInfoStore createInfo = services.GetRequiredService<PlayerCreateInfoStore>();

        await report.MeasureAsync("playercreateinfo", () =>
            createInfo.LoadAsync(worldConnection, cancellationToken)).ConfigureAwait(false);

        if (createInfo.Count == 0)
        {
            throw new InvalidOperationException(
                "playercreateinfo is empty — character creation would reject every race. Import it with: " +
                "docker exec -i wowemu-mysql mysql -uroot -pwowemu wowemu_world " +
                "< database-wotlk/sql/base/playercreateinfo.sql");
        }

        PlayerStatsStore stats = services.GetRequiredService<PlayerStatsStore>();
        PlayerXpStore experience = services.GetRequiredService<PlayerXpStore>();
        GraveyardStore graveyards = services.GetRequiredService<GraveyardStore>();

        await report.MeasureAsync("player stats", async () =>
        {
            await stats.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
            await experience.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
            await graveyards.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (experience.Count == 0)
        {
            // Not fatal, but worth saying loudly: without it nobody gains a level, and the symptom
            // is an experience bar that fills and stops rather than an error.
            Log.ExperienceTableMissing(logger);
        }

        if (stats.LevelStatCount == 0 || stats.ClassStatCount == 0)
        {
            throw new InvalidOperationException(
                "player_levelstats or player_classlevelstats is empty — characters would enter the world " +
                "with no health. Import both with: tools/db/import-world.sh");
        }

        await report.MeasureAsync("world content", () =>
            LoadCreatureContentAsync(services, worldConnection, options, logger, report, cancellationToken))
            .ConfigureAwait(false);

        WorldContent content = services.GetRequiredService<WorldContent>();

        if (content.Terrain.IsAvailable)
        {
            // Counted once here rather than inside the log call: enumerating 5,000 files is too
            // expensive to do speculatively if the log level turns out to be disabled.
            int tileCount = content.Terrain.CountTileFiles();
            Log.TerrainAvailable(logger, tileCount, content.Terrain.MapsDirectory);
        }
        else
        {
            // Not fatal: a character still logs in and walks. The server just cannot check any of it.
            Log.TerrainMissing(logger, content.Terrain.MapsDirectory);
        }

        Log.ContentLoaded(
            logger,
            content.Stores.Races.Count,
            content.Stores.Classes.Count,
            content.Stores.Maps.Count,
            stats.LevelStatCount);

        SpellStores spells = services.GetRequiredService<SpellStores>();

        Log.SpellDataLoaded(
            logger, spells.Spells.Count, spells.CastTimes.Count, spells.Ranges.Count, spells.Durations.Count);

        Log.ExperienceTableLoaded(logger, experience.Count, experience.MaxLevel);

        Log.GraveyardsLoaded(
            logger, graveyards.Count, graveyards.ZoneCount, content.Stores.WorldSafeLocs.Count);

        // Last, so the total includes everything above it. A budget nobody measures is a number in
        // a document — PLAN.md §6 Phase 4 allows thirty seconds, and the tables read here have
        // roughly doubled since that was written.
        // Built into a local: the analyzer objects to work inside a log call, and the summary sorts
        // and formats the phase list.
        string summary = report.Summary();

        if (report.OverBudget)
        {
            Log.StartupOverBudget(logger, summary, StartupReport.Budget.TotalSeconds);
        }
        else
        {
            Log.StartupComplete(logger, summary);
        }
    }

    /// <summary>
    /// Loads the tables creature and gameobject spawning read, and reports how long it took.
    /// </summary>
    /// <remarks>
    /// Timed because this is the first load large enough to matter — 176,000 rows against the 5,800
    /// everything before it read — and PLAN.md §6 Phase 4 budgets the whole startup at under 30
    /// seconds. A number in the log is what turns that budget into something anyone can check.
    /// <para>
    /// Empty tables are fatal for the same reason <c>playercreateinfo</c> is: a server that starts
    /// and only then turns out to have no creatures looks like a visibility bug, and would be
    /// debugged as one.
    /// </para>
    /// </remarks>
    private static async Task LoadCreatureContentAsync(
        IServiceProvider services,
        string worldConnection,
        WorldServerOptions options,
        ILogger logger,
        StartupReport report,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        CreatureTemplateStore templates = services.GetRequiredService<CreatureTemplateStore>();
        await templates.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        CreatureStatsStore creatureStats = services.GetRequiredService<CreatureStatsStore>();
        await creatureStats.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        CreatureSpawnStore spawns = services.GetRequiredService<CreatureSpawnStore>();
        await spawns.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        // Patrol routes, and the addon rows that say which spawn walks which. Not fatal when
        // empty: a world with no waypoints is a world where 5,290 guards stand still, which is
        // exactly what it was before these were vendored — a degraded world, not a broken one.
        WaypointStore waypoints = services.GetRequiredService<WaypointStore>();
        await waypoints.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        CreatureAddonStore addons = services.GetRequiredService<CreatureAddonStore>();
        await addons.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        CreatureEquipStore equipment = services.GetRequiredService<CreatureEquipStore>();
        await equipment.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        if (templates.TemplateCount == 0 || spawns.Count == 0 || creatureStats.Count == 0)
        {
            throw new InvalidOperationException(
                "creature_template, creature or creature_classlevelstats is empty — the world would " +
                "have no creatures in it. Import them with: tools/db/import-world.sh");
        }

        GameObjectTemplateStore objectTemplates = services.GetRequiredService<GameObjectTemplateStore>();
        await objectTemplates.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        GameObjectSpawnStore objectSpawns = services.GetRequiredService<GameObjectSpawnStore>();
        await objectSpawns.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        if (objectTemplates.Count == 0 || objectSpawns.Count == 0)
        {
            throw new InvalidOperationException(
                "gameobject_template or gameobject is empty — the world would have no doors, chests " +
                "or mailboxes in it. Import them with: tools/db/import-world.sh");
        }

        ItemTemplateStore items = services.GetRequiredService<ItemTemplateStore>();
        await items.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                "item_template is empty — nothing could be looted, equipped or sold. Import it " +
                "with: tools/db/import-world.sh");
        }

        LootStore creatureLoot = services.GetRequiredKeyedService<LootStore>("creature_loot");
        LootStore lootReferences = services.GetRequiredKeyedService<LootStore>("reference_loot");

        await creatureLoot.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        await lootReferences.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        if (creatureLoot.Count == 0)
        {
            throw new InvalidOperationException(
                "creature_loot_template is empty — nothing in the world would drop anything. " +
                "Import it with: tools/db/import-world.sh");
        }

        QuestStore quests = services.GetRequiredService<QuestStore>();
        QuestRelationStore questStarters = services.GetRequiredKeyedService<QuestRelationStore>("quest_starters");
        QuestRelationStore questEnders = services.GetRequiredKeyedService<QuestRelationStore>("quest_enders");

        quests.IgnoreAutoAccept = options.IgnoreAutoAccept;

        await quests.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        await questStarters.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        await questEnders.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        QuestRelationStore objectStarters =
            services.GetRequiredKeyedService<QuestRelationStore>("go_quest_starters");
        QuestRelationStore objectEnders =
            services.GetRequiredKeyedService<QuestRelationStore>("go_quest_enders");

        await objectStarters.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        await objectEnders.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        if (quests.Count == 0)
        {
            throw new InvalidOperationException(
                "quest_template is empty — no NPC would have anything to offer. Import it with: " +
                "tools/db/import-world.sh");
        }

        GossipStore gossip = services.GetRequiredService<GossipStore>();
        VendorStore vendors = services.GetRequiredService<VendorStore>();

        PlayerSpellStore startingSpells = services.GetRequiredService<PlayerSpellStore>();
        TrainerStore trainers = services.GetRequiredService<TrainerStore>();
        SpellRankStore spellRanks = services.GetRequiredService<SpellRankStore>();

        await gossip.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        await vendors.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        PlayerActionStore startingActions = services.GetRequiredService<PlayerActionStore>();

        await startingSpells.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        await startingActions.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        await trainers.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);
        await spellRanks.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        // Measured into a local rather than inline: the analyzer objects to work inside a log call,
        // and the elapsed time has to be taken at the same point whether or not anyone is listening.
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        Log.CreatureContentLoaded(
            logger,
            templates.TemplateCount,
            templates.ModelCount,
            spawns.Count,
            spawns.MapCount,
            elapsedMs);

        Log.GameObjectContentLoaded(logger, objectTemplates.Count, objectSpawns.Count, objectSpawns.MapCount);

        Log.WaypointContentLoaded(logger, waypoints.Count, waypoints.PathCount, addons.Count, addons.PathCount);

        Log.CreatureEquipmentLoaded(logger, equipment.Count, equipment.EntryCount);

        // Grid indexes up front, now that the spawns they read are in memory. Built lazily this used
        // to land on whichever tick a player first reached a map — ~20 ms inside a 50 ms login tick,
        // for Eastern Kingdoms alone.
        long indexStarted = Stopwatch.GetTimestamp();

        int creatureMaps = services.GetRequiredService<CreatureGridLoader>().BuildIndexes();
        int objectMaps = services.GetRequiredService<GameObjectGridLoader>().BuildIndexes();

        double indexElapsedMs = Stopwatch.GetElapsedTime(indexStarted).TotalMilliseconds;
        Log.SpawnIndexesBuilt(logger, creatureMaps, objectMaps, indexElapsedMs);
        Log.ItemTemplatesLoaded(logger, items.Count);
        Log.QuestsLoaded(logger, quests.Count, questStarters.RowCount, questEnders.RowCount);
        Log.GossipLoaded(logger, gossip.MenuCount, gossip.OptionCount, gossip.TextCount, vendors.RowCount);
        Log.SpellsAndTrainersLoaded(
            logger, startingSpells.Count, startingActions.Count, trainers.RowCount, trainers.Count);
        Log.LootTemplatesLoaded(
            logger, creatureLoot.RowCount, creatureLoot.Count, lootReferences.RowCount, lootReferences.Count);
    }
}
