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

        PlayerCreateInfoStore createInfo = services.GetRequiredService<PlayerCreateInfoStore>();

        await createInfo
            .LoadAsync(PlayerCreateInfoStore.ResolveConnectionString(options.WorldConnectionString), cancellationToken)
            .ConfigureAwait(false);

        if (createInfo.Count == 0)
        {
            throw new InvalidOperationException(
                "playercreateinfo is empty — character creation would reject every race. Import it with: " +
                "docker exec -i wowemu-mysql mysql -uroot -pwowemu wowemu_world " +
                "< database-wotlk/sql/base/playercreateinfo.sql");
        }
    }
}
