using MySql.Data.MySqlClient;
using WowEmu.Data.Db;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Marks a test that reads the <c>world</c> database.
/// </summary>
/// <remarks>
/// Same reasoning as <see cref="RequiresClientDataFactAttribute"/>: the database is stood up with
/// Docker and seeded by <c>tools/db/import-world.sh</c>, and not every machine running the suite
/// will have done that. Skipping beats a red suite that means "you have not run Docker".
/// </remarks>
public sealed class RequiresWorldDatabaseFactAttribute : FactAttribute
{
    public RequiresWorldDatabaseFactAttribute()
    {
        if (!WorldDatabase.Available)
        {
            Skip = "no world database — start it with docker compose up -d, then tools/db/import-world.sh";
        }
    }
}

/// <summary>Reachability of the <c>world</c> database, probed once per test run.</summary>
internal static class WorldDatabase
{
    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            using MySqlConnection connection = new(ConnectionString);
            connection.Open();

            using MySqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM creature_template";

            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        }
        catch (MySqlException)
        {
            // Not running, not seeded, or no such table. All three mean the same thing here.
            return false;
        }
    });

    public static string ConnectionString => PlayerCreateInfoStore.ResolveConnectionString();

    public static bool Available => Probe.Value;
}

/// <summary>
/// The creature stores, read against the real vendored rows.
/// </summary>
/// <remarks>
/// These exist because the column types are the part that cannot be reasoned about from the schema
/// alone. <c>unit_flags</c> reaches past <see cref="int.MaxValue"/> and <c>phaseMask</c> uses the
/// full 32 bits, so a signed reader throws — but only on the handful of rows that use the high bit,
/// which is exactly the kind of failure that survives a hand-written smoke test.
/// </remarks>
public sealed class CreatureStoreTests
{
    private static CancellationToken TestToken => CancellationToken.None;

    [RequiresWorldDatabaseFact]
    public async Task Templates_LoadEveryRowWithoutOverflowing()
    {
        CreatureTemplateStore store = new();
        await store.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(store.TemplateCount > 29_000, $"only {store.TemplateCount} templates");
        Assert.True(store.ModelCount > 24_000, $"only {store.ModelCount} models");
    }

    /// <summary>A known entry, checked field by field against what the dump says.</summary>
    [RequiresWorldDatabaseFact]
    public async Task Templates_ReadTheColumnsTheyClaimTo()
    {
        CreatureTemplateStore store = new();
        await store.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        // 2843 is "Mist Howler" in Durotar — the first row of the creature table's first spawn.
        Assert.True(store.TryGetTemplate(2843, out CreatureTemplate? template));
        Assert.NotNull(template);

        Assert.Equal(2843u, template.Entry);
        Assert.NotEmpty(template.Name);
        Assert.InRange(template.MinLevel, (byte)1, (byte)100);
        Assert.True(template.MaxLevel >= template.MinLevel);
        Assert.InRange(template.Expansion, (byte)0, (byte)2);
        Assert.Contains(template.UnitClass, (byte[])[1, 2, 4, 8]);
        Assert.True(template.Scale > 0f);
    }

    /// <summary>
    /// Every template's model resolves. A display id with no <c>creature_model_info</c> row is a
    /// creature that spawns with no size and cannot be clicked.
    /// </summary>
    [RequiresWorldDatabaseFact]
    public async Task EveryTemplateModel_HasModelInfo()
    {
        CreatureTemplateStore store = new();
        CreatureSpawnStore spawns = new();

        await store.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await spawns.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        List<string> missing = [];

        foreach (uint mapId in (uint[])[0, 1, 530, 571])
        {
            foreach (CreatureSpawn spawn in spawns.ForMap(mapId))
            {
                if (!store.TryGetTemplate(spawn.Entry, out CreatureTemplate? template) || template is null)
                {
                    missing.Add($"spawn {spawn.SpawnId}: no template for entry {spawn.Entry}");
                    continue;
                }

                // The first valid slot, deterministically — this is a data check, not a spawn.
                uint displayId = spawn.ModelId != 0
                    ? spawn.ModelId
                    : template.GetRandomValidModelId((low, _) => low);

                if (displayId != 0 && !store.TryGetModel(displayId, out _))
                {
                    missing.Add($"entry {spawn.Entry}: no model info for display {displayId}");
                }

                if (missing.Count > 10)
                {
                    break;
                }
            }
        }

        Assert.Empty(missing);
    }

    [RequiresWorldDatabaseFact]
    public async Task Spawns_LoadAndAreFiledByMap()
    {
        CreatureSpawnStore store = new();
        await store.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(store.Count > 140_000, $"only {store.Count} spawns");
        Assert.True(store.MapCount > 100, $"only {store.MapCount} maps");

        // Eastern Kingdoms and Kalimdor are the two continents; both are densely populated, and a
        // zero here means the map column was read wrongly rather than that the world is empty.
        Assert.NotEmpty(store.ForMap(0));
        Assert.NotEmpty(store.ForMap(1));

        // A map nobody spawns anything on answers empty rather than throwing.
        Assert.Empty(store.ForMap(9999));

        foreach (CreatureSpawn spawn in store.ForMap(0))
        {
            Assert.Equal(0u, spawn.MapId);
        }
    }

    /// <summary>
    /// Every spawn's entry exists. A spawn with no template is a creature that cannot be built, and
    /// the grid loader would skip it silently.
    /// </summary>
    [RequiresWorldDatabaseFact]
    public async Task EverySpawn_HasATemplate()
    {
        CreatureTemplateStore templates = new();
        CreatureSpawnStore spawns = new();

        await templates.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await spawns.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        List<uint> orphaned = [];

        foreach (uint mapId in (uint[])[0, 1])
        {
            foreach (CreatureSpawn spawn in spawns.ForMap(mapId))
            {
                if (!templates.TryGetTemplate(spawn.Entry, out _))
                {
                    orphaned.Add(spawn.Entry);
                }
            }
        }

        Assert.Empty(orphaned.Distinct().Take(10));
    }

    /// <summary>
    /// Base stats cover every level a template can roll.
    /// </summary>
    /// <remarks>
    /// A gap here is a creature the factory refuses to build, and the zone it lives in comes up
    /// short by one creature with nothing but a debug line to say so.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task BaseStats_CoverEveryLevelATemplateCanRoll()
    {
        CreatureTemplateStore templates = new();
        CreatureStatsStore stats = new();

        await templates.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await stats.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.Equal(400, stats.Count);

        // Level 1 and level 83 (the highest a WotLK creature reaches) for all four unit classes.
        foreach (byte unitClass in (byte[])[1, 2, 4, 8])
        {
            Assert.True(stats.TryGet(1, unitClass, out CreatureBaseStats low), $"no level 1 for class {unitClass}");
            Assert.True(stats.TryGet(83, unitClass, out CreatureBaseStats high), $"no level 83 for class {unitClass}");

            Assert.True(low.BaseHealthClassic > 0);
            Assert.True(high.BaseHealthWrath > low.BaseHealthClassic);
        }
    }

    /// <summary>The expansion slot is what makes a level-70 mob in Outland different from one in Azeroth.</summary>
    [RequiresWorldDatabaseFact]
    public async Task BaseHealth_DiffersByExpansion()
    {
        CreatureStatsStore stats = new();
        await stats.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(stats.TryGet(70, 1, out CreatureBaseStats atSeventy));

        Assert.Equal(atSeventy.BaseHealthClassic, atSeventy.BaseHealthFor(0));
        Assert.Equal(atSeventy.BaseHealthBurningCrusade, atSeventy.BaseHealthFor(1));
        Assert.Equal(atSeventy.BaseHealthWrath, atSeventy.BaseHealthFor(2));

        // An out-of-range expansion falls back to classic rather than indexing off the end.
        Assert.Equal(atSeventy.BaseHealthClassic, atSeventy.BaseHealthFor(9));
    }
}
