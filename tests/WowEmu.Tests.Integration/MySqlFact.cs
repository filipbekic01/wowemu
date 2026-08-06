using System.Net.Sockets;

namespace WowEmu.Tests.Integration;

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when there is no MySQL to talk to.
/// </summary>
/// <remarks>
/// <para>
/// Integration tests need a database, and a developer who has not run <c>docker compose up -d</c>
/// should get a skipped test, not a failing suite — otherwise the first instinct is to stop running
/// the suite at all.
/// </para>
/// <para>
/// The skip is decided at discovery time rather than inside the test body, because dynamic skipping
/// in xunit v2 depends on runner support that the VSTest adapter does not reliably provide.
/// </para>
/// <para>
/// Setting <c>WOWEMU_INTEGRATION=1</c> turns the skip off: in CI a database that fails to answer is
/// a broken pipeline, and quietly reporting a green run with every test skipped is exactly the
/// failure this whole phase exists to prevent.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (!TestDatabase.Required && !TestDatabase.IsReachable.Value)
        {
            Skip = $"No MySQL on {TestDatabase.Host}:{TestDatabase.Port} — run: docker compose up -d";
        }
    }
}

/// <summary>Where the integration tests expect to find MySQL, and whether it is there.</summary>
internal static class TestDatabase
{
    /// <summary>Host of the server under test. Matches <c>docker-compose.yml</c>.</summary>
    public static string Host =>
        Environment.GetEnvironmentVariable("WOWEMU_MYSQL_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("WOWEMU_MYSQL_PORT"), out int port) ? port : 3306;

    /// <summary>Root password, for creating and dropping the throwaway schema.</summary>
    public static string RootPassword =>
        Environment.GetEnvironmentVariable("WOWEMU_MYSQL_ROOT_PASSWORD") is { Length: > 0 } password
            ? password
            : "wowemu";

    /// <summary>
    /// A schema of its own, dropped and recreated per run. Never <c>wowemu_auth</c>: a test that
    /// truncates the developer's accounts would be a memorable way to learn this lesson.
    /// </summary>
    public const string Schema = "wowemu_auth_it";

    /// <summary>True when the environment insists the database must be present — CI does.</summary>
    public static bool Required =>
        Environment.GetEnvironmentVariable("WOWEMU_INTEGRATION") is "1" or "true";

    /// <summary>
    /// Probed once per process. A TCP connect is enough — if the port answers, the fixture's own
    /// connection will produce a far better error message than a skip would.
    /// </summary>
    public static readonly Lazy<bool> IsReachable = new(() =>
    {
        try
        {
            using TcpClient client = new();
            return client.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or AggregateException)
        {
            return false;
        }
    });

    /// <summary>Connection string for the throwaway schema.</summary>
    public static string ConnectionString =>
        $"server={Host};port={Port};database={Schema};user=root;password={RootPassword}";

    /// <summary>Connection string with no schema selected, for CREATE / DROP DATABASE.</summary>
    public static string ServerConnectionString =>
        $"server={Host};port={Port};user=root;password={RootPassword}";
}
