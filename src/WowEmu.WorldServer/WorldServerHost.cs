using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WowEmu.Data.Db;
using WowEmu.Network;

namespace WowEmu.WorldServer;

/// <summary>
/// Accepts world connections and runs one <see cref="WorldSession"/> per client.
/// </summary>
/// <remarks>
/// Phase 3 gives every connection its own task, as the logon server does. The tick loop and map
/// workers of PLAN.md §4.2 arrive in Phase 5-6; the <c>TickScheduler</c> they will run on already
/// exists in <c>WowEmu.Core</c>, so sessions can be moved onto it without reshaping this host.
/// </remarks>
public sealed class WorldServerHost(
    IOptions<WorldServerOptions> options,
    IAccountRepository accounts,
    ICharacterRepository characters,
    PlayerCreateInfoStore createInfo,
    ILogger<WorldServerHost> logger,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly WorldServerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IPAddress address = IPAddress.Parse(_options.BindAddress);
        using Socket listener = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(new IPEndPoint(address, _options.Port));
        listener.Listen(128);

        Log.Listening(logger, address, _options.Port, _options.RealmId);
        Log.CreateInfoLoaded(logger, createInfo.Count);

        ILogger sessionLogger = loggerFactory.CreateLogger<WorldSession>();

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
        using WorldConnection connection = new(client);

        try
        {
            client.NoDelay = true;
            Log.ClientConnected(logger, client.RemoteEndPoint);

            WorldSession session = new(connection, accounts, characters, createInfo, _options, sessionLogger);
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidDataException)
        {
            // Disconnects and malformed framing are routine; neither is worth a stack trace.
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
