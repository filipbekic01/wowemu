using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WowEmu.Data.Db;

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

        string worldConnection = PlayerCreateInfoStore.ResolveConnectionString(options.WorldConnectionString);

        PlayerCreateInfoStore createInfo = services.GetRequiredService<PlayerCreateInfoStore>();
        await createInfo.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        if (createInfo.Count == 0)
        {
            throw new InvalidOperationException(
                "playercreateinfo is empty — character creation would reject every race. Import it with: " +
                "docker exec -i wowemu-mysql mysql -uroot -pwowemu wowemu_world " +
                "< database-wotlk/sql/base/playercreateinfo.sql");
        }

        PlayerStatsStore stats = services.GetRequiredService<PlayerStatsStore>();
        await stats.LoadAsync(worldConnection, cancellationToken).ConfigureAwait(false);

        if (stats.LevelStatCount == 0 || stats.ClassStatCount == 0)
        {
            throw new InvalidOperationException(
                "player_levelstats or player_classlevelstats is empty — characters would enter the world " +
                "with no health. Import both from database-wotlk/sql/base/.");
        }

        WorldContent content = services.GetRequiredService<WorldContent>();

        Log.ContentLoaded(
            logger,
            content.Stores.Races.Count,
            content.Stores.Classes.Count,
            content.Stores.Maps.Count,
            stats.LevelStatCount);
    }
}
