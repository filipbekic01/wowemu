using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WowEmu.Data.Db;

namespace WowEmu.AuthServer;

/// <summary>
/// Accepts logon connections and runs one <see cref="AuthSession"/> per client.
/// </summary>
/// <remarks>
/// AzerothCore pins the logon server to exactly one network thread
/// (<c>AuthSocketMgr.h:48</c> hard-codes <c>new NetworkThread&lt;AuthSession&gt;[1]</c>), which is
/// why nothing in its <c>AuthSession</c> takes a lock. Here each connection is an independent task
/// and shares no mutable state except <see cref="AccountStore"/>, which is itself concurrent — so
/// the invariant holds for a different reason, not by accident.
/// </remarks>
public sealed class AuthServerHost(
    IOptions<AuthServerOptions> options,
    IAccountRepository accounts,
    IBuildRepository builds,
    RealmList realms,
    ILogger<AuthServerHost> logger,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly AuthServerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IPAddress address = IPAddress.Parse(_options.BindAddress);
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(new IPEndPoint(address, _options.Port));
        listener.Listen(128);

        int accountCount = await accounts.CountAsync(stoppingToken).ConfigureAwait(false);

        Log.Listening(logger, address, _options.Port);
        Log.ServerSummary(logger, accountCount, realms.Realms.Count);
        Log.RealmlistHint(logger);

        ILogger sessionLogger = loggerFactory.CreateLogger<AuthSession>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket client = await listener.AcceptAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, sessionLogger, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log.ShuttingDown(logger);
        }
    }

    private async Task HandleClientAsync(Socket client, ILogger sessionLogger, CancellationToken cancellationToken)
    {
        try
        {
            client.NoDelay = true;
            Log.ClientConnected(logger, client.RemoteEndPoint);

            AuthSession session = new(client, accounts, builds, realms, sessionLogger);
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            // Client disconnected; nothing to report.
        }
        catch (Exception ex)
        {
            Log.SessionFailed(logger, ex, client.RemoteEndPoint);
        }
        finally
        {
            try
            {
                client.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Already gone.
            }

            client.Dispose();
        }
    }
}
