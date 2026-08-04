using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace WowEmu.Data.Db;

/// <summary>Wiring for the <c>auth</c> database.</summary>
public static class AuthDatabase
{
    /// <summary>
    /// Environment variable that overrides the connection string everywhere — server, CLI, and the
    /// <c>dotnet ef</c> design-time tooling, which has no <c>appsettings.json</c> of its own.
    /// </summary>
    public const string ConnectionStringVariable = "WOWEMU_AUTH_CONNECTION";

    /// <summary>Matches the MySQL service in <c>docker-compose.yml</c>. Development only.</summary>
    public const string DefaultConnectionString =
        "server=127.0.0.1;port=3306;database=wowemu_auth;user=wowemu;password=wowemu";

    /// <summary>
    /// Registers the context factory and the repositories. Everything is a singleton over an
    /// <see cref="IDbContextFactory{TContext}"/>; nothing holds a context open.
    /// </summary>
    public static IServiceCollection AddAuthDatabase(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContextFactory<AuthDbContext>(options => options.UseMySQL(connectionString));
        services.AddSingleton<IAccountRepository, AccountRepository>();
        services.AddSingleton<IRealmRepository, RealmRepository>();
        services.AddSingleton<IBuildRepository, BuildRepository>();

        return services;
    }

    /// <summary>
    /// The connection string to use when nothing else supplies one: the environment variable if it
    /// is set, otherwise the local Docker default.
    /// </summary>
    public static string ResolveConnectionString(string? configured = null)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured;
    }
}

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build a context without starting a host.
/// </summary>
/// <remarks>
/// Without this, the tooling would have to boot the auth server's host — which binds port 3724 and
/// connects to the database — just to read the model.
/// </remarks>
public sealed class DesignTimeAuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<AuthDbContext> options = new();
        options.UseMySQL(AuthDatabase.ResolveConnectionString());
        return new AuthDbContext(options.Options);
    }
}
