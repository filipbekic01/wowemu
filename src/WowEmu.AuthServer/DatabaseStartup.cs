using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WowEmu.Data.Db;

namespace WowEmu.AuthServer;

/// <summary>
/// Brings the <c>auth</c> schema up to date before anything starts listening.
/// </summary>
/// <remarks>
/// This runs before <c>host.RunAsync()</c> rather than as a hosted service so that a missing or
/// unreachable database is a startup failure with a clear message, not a stream of errors from
/// sessions that have already been accepted.
/// </remarks>
internal static class DatabaseStartup
{
    public static async Task PrepareAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        AuthServerOptions options = services.GetRequiredService<IOptions<AuthServerOptions>>().Value;
        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Database");

        if (options.ApplyMigrationsOnStartup)
        {
            IDbContextFactory<AuthDbContext> contextFactory =
                services.GetRequiredService<IDbContextFactory<AuthDbContext>>();

            await using AuthDbContext context =
                await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> pending =
                [.. await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)];

            if (pending.Count > 0)
            {
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                Log.MigrationsApplied(logger, pending.Count);
            }
            else
            {
                Log.SchemaUpToDate(logger);
            }
        }

        IAccountRepository accounts = services.GetRequiredService<IAccountRepository>();

        if (await accounts.CountAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            Log.NoAccountsConfigured(logger);
        }
    }
}
