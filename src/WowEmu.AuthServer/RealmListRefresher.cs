using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WowEmu.Data.Db;

namespace WowEmu.AuthServer;

/// <summary>
/// Keeps <see cref="RealmList"/> in step with the <c>realmlist</c> table.
/// </summary>
/// <remarks>
/// The first load happens in <see cref="StartAsync"/>, before the listener is accepting, so the
/// server never answers a realm-list request with an empty list it simply hasn't fetched yet. A
/// database that is unreachable at startup therefore fails the host immediately rather than
/// presenting a working login with no realms.
/// </remarks>
public sealed class RealmListRefresher(
    IRealmRepository realmRepository,
    RealmList realmList,
    IOptions<AuthServerOptions> options,
    ILogger<RealmListRefresher> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(options.Value.RealmRefreshSeconds);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await LoadAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<RealmRegistration> registrations =
                await realmRepository.ListAsync(cancellationToken).ConfigureAwait(false);

            realmList.Update(registrations);
            Log.RealmsLoaded(logger, registrations.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A refresh failure must not take the server down; the cached list stays serving.
            Log.RealmRefreshFailed(logger, exception);

            if (realmList.Realms.Count == 0)
            {
                throw;
            }
        }
    }
}
