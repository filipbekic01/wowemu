using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using WowEmu.AuthServer;
using WowEmu.Data.Db;

namespace WowEmu.Tests.Integration;

/// <summary>
/// A real logon server, in this process, on its own port, against its own throwaway schema.
/// </summary>
/// <remarks>
/// <para>
/// The point is to test the wiring the unit tests cannot: EF migrations actually applying, the
/// repositories actually reaching MySQL, the listener actually accepting, and a session key written
/// by one connection actually being visible to the next one. Every one of those is a seam where the
/// pieces are individually correct and the assembly is not.
/// </para>
/// <para>
/// Hosted in-process rather than launched as a child process so that a server-side exception
/// surfaces as a failing test with a stack trace instead of a timeout.
/// </para>
/// </remarks>
public sealed class AuthServerFixture : IAsyncLifetime
{
    private IHost? _host;

    /// <summary>Port the logon server is listening on. Chosen at run time, never 3724.</summary>
    public int Port { get; private set; }

    /// <summary>Account the tests log in as. Created fresh with the schema.</summary>
    public const string Username = "ITTEST";

    public const string Password = "ITPASSWORD";

    public async Task InitializeAsync()
    {
        await ResetSchemaAsync().ConfigureAwait(false);

        Port = FindFreePort();

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuthServer:BindAddress"] = "127.0.0.1",
            ["AuthServer:Port"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["AuthServer:ConnectionString"] = TestDatabase.ConnectionString,

            // Long enough that the refresher never fires a second time during a test run; the realm
            // list is read once during startup, which is all these tests need.
            ["AuthServer:RealmRefreshSeconds"] = "3600",
        });

        builder.Services
            .AddOptions<AuthServerOptions>()
            .Bind(builder.Configuration.GetSection(AuthServerOptions.SectionName))
            .ValidateDataAnnotations();

        // Warning and above: a passing run should be quiet, and a failing one should still say why.
        builder.Services.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));

        builder.Services.AddAuthDatabase(TestDatabase.ConnectionString);
        builder.Services.AddSingleton<RealmList>();
        builder.Services.AddHostedService<RealmListRefresher>();
        builder.Services.AddHostedService<AuthServerHost>();

        _host = builder.Build();

        // The schema is created here rather than by the server's own DatabaseStartup, which is
        // internal. Same call, one layer down.
        IDbContextFactory<AuthDbContext> contextFactory =
            _host.Services.GetRequiredService<IDbContextFactory<AuthDbContext>>();

        await using (AuthDbContext context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        IAccountRepository accounts = _host.Services.GetRequiredService<IAccountRepository>();
        await accounts.CreateAsync(Username, Password).ConfigureAwait(false);

        await _host.StartAsync().ConfigureAwait(false);
        await WaitUntilListeningAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
        }

        await DropSchemaAsync().ConfigureAwait(false);
    }

    /// <summary>Opens a connection to the logon server.</summary>
    public async Task<Socket> ConnectAsync()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        await socket.ConnectAsync(IPAddress.Loopback, Port).ConfigureAwait(false);
        return socket;
    }

    /// <summary>
    /// Drops and recreates the schema, so a run never inherits rows from the last one.
    /// </summary>
    /// <remarks>
    /// <c>utf8mb4_bin</c> matches <c>docker-compose.yml</c>: usernames are compared byte-for-byte
    /// after being uppercased, and a case-insensitive collation would silently make the unique index
    /// on <c>username</c> mean something different here than in production.
    /// </remarks>
    private static async Task ResetSchemaAsync()
    {
        await using MySqlConnection connection = new(TestDatabase.ServerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"DROP DATABASE IF EXISTS `{TestDatabase.Schema}`; " +
            $"CREATE DATABASE `{TestDatabase.Schema}` CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;";

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task DropSchemaAsync()
    {
        try
        {
            await using MySqlConnection connection = new(TestDatabase.ServerConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await using MySqlCommand command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS `{TestDatabase.Schema}`;";

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (MySqlException)
        {
            // Teardown only. A database that has already gone away is not a test failure.
        }
    }

    /// <summary>
    /// Asks the OS for an unused port by binding one and letting go.
    /// </summary>
    /// <remarks>
    /// Racy in principle — something else could take the port in the gap — but the alternative is a
    /// fixed port, which collides with a developer's own running server every single time rather
    /// than approximately never.
    /// </remarks>
    private static int FindFreePort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>
    /// <see cref="IHost.StartAsync"/> returns once the hosted service has been started, not once its
    /// listener is accepting — so poll until a connection succeeds.
    /// </summary>
    private async Task WaitUntilListeningAsync()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using Socket socket = await ConnectAsync().ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"the logon server never started listening on port {Port}");
    }
}

/// <summary>
/// Shares one server across the logon tests; starting it per test would dominate the run.
/// </summary>
/// <remarks>
/// Named "Suite" rather than the xunit-idiomatic "Collection" only because CA1711 reserves that
/// suffix for types that are actually collections. The name on the wire is <see cref="Name"/>.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AuthServerSuite : ICollectionFixture<AuthServerFixture>
{
    public const string Name = "auth-server";
}
