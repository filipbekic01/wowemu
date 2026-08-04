using System.ComponentModel.DataAnnotations;

namespace WowEmu.AuthServer;

/// <summary>Configuration for the logon server, bound from <c>appsettings.json</c>.</summary>
public sealed class AuthServerOptions
{
    public const string SectionName = "AuthServer";

    /// <summary>Address to listen on. The retail client connects to port 3724.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Listen port. 3724 is what <c>realmlist.wtf</c> points at.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 3724;

    /// <summary>
    /// Connection string for the <c>auth</c> database. Empty means "use the local Docker default";
    /// the <c>WOWEMU_AUTH_CONNECTION</c> environment variable overrides both.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>How often the cached realm list is re-read from the database.</summary>
    [Range(1, 3600)]
    public int RealmRefreshSeconds { get; set; } = 60;

    /// <summary>
    /// Apply pending EF Core migrations at startup. Convenient for a single-node development box;
    /// turn it off once more than one process could race to migrate the same schema.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;
}
