using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WowEmu.Data.Db;
using WowEmu.Game.Maps;
using WowEmu.Network;

namespace WowEmu.WorldServer;

/// <summary>
/// Accepts world connections and runs one <see cref="WorldSession"/> per client.
/// </summary>
/// <remarks>
/// Every connection gets its own task, but that task only reads, decrypts and queues: handling
/// happens on the world tick or a map worker. See <see cref="WorldLoop"/> for why that separation
/// is the whole safety story.
/// </remarks>
public sealed class WorldServerHost(
    IOptions<WorldServerOptions> options,
    IAccountRepository accounts,
    ICharacterRepository characters,
    PlayerCreateInfoStore createInfo,
    IInventoryRepository inventory,
    ItemGuidGenerator itemGuids,
    WorldContent world,
    MapManager maps,
    SessionRegistry sessions,
    WorldLoop worldLoop,
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

            WorldSession session = new(
                connection, accounts, characters, createInfo, inventory, itemGuids, world, maps,
                _options, sessionLogger);

            // Bound to the world tick before it is registered, so the first packet it queues already
            // has somewhere to resume.
            session.AttachTo(worldLoop.Scheduler);
            sessions.Add(session);

            try
            {
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sessions.Remove(session);

                // A client that vanishes — alt-F4, a dropped connection — never sends a logout, so
                // the save has to happen here too. It runs on the world loop rather than on this
                // task, because taking a player off a map is map state.
                await session.DisconnectAsync().ConfigureAwait(false);
            }
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
