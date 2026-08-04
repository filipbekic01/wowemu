using System.ComponentModel.DataAnnotations;

namespace WowEmu.WorldServer;

/// <summary>Configuration for the world server, bound from <c>appsettings.json</c>.</summary>
public sealed class WorldServerOptions
{
    public const string SectionName = "WorldServer";

    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Listen port. Must match the port the realm advertises in <c>realmlist</c>.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 8085;

    /// <summary>
    /// Which realm this process serves. The client sends the realm id it picked and upstream
    /// rejects a mismatch, so this has to agree with the <c>realmlist</c> row.
    /// </summary>
    public byte RealmId { get; set; } = 1;

    /// <summary>
    /// Connection string for the <c>auth</c> database — the world server reads the session key the
    /// logon server stored there. Empty means the local Docker default.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Sent in <c>SMSG_CLIENTCACHE_VERSION</c>. Bumping it makes clients drop their cached
    /// <c>WDB</c> files, which is how you recover from stale cached data after content changes.
    /// </summary>
    public uint ClientCacheVersion { get; set; }

    /// <summary>Expansion level offered to clients: 0 vanilla, 1 TBC, 2 WotLK.</summary>
    [Range(0, 2)]
    public byte Expansion { get; set; } = 2;
}
