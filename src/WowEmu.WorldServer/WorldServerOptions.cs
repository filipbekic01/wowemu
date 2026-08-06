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

    /// <summary>
    /// Make every quest wait for the player to press Accept.
    /// </summary>
    /// <remarks>
    /// <c>Quests.IgnoreAutoAccept</c> in <c>worldserver.conf</c>, and <b>false to match a stock
    /// realm</b>. Left false, a quest whose <c>Flags</c> carry <c>QUEST_FLAGS_AUTO_ACCEPT</c> — or
    /// whose <c>SpecialFlags</c> ask for it — goes into the log the moment its window opens, with
    /// no click. That is not a bug: the 3.3.5a client reads the same flag, treats the quest as
    /// already taken, and never sends an accept for it, so if the server waits then nobody takes
    /// the quest at all. "A Threat Within" (783), the first quest a human sees, is one of these.
    /// Set this true to have the client ask first, and accept that the realm no longer behaves
    /// like upstream.
    /// </remarks>
    public bool IgnoreAutoAccept { get; set; }

    /// <summary>Connection string for this realm's <c>characters</c> database.</summary>
    public string CharactersConnectionString { get; set; } = string.Empty;

    /// <summary>Connection string for the read-only <c>world</c> content database.</summary>
    public string WorldConnectionString { get; set; } = string.Empty;

    /// <summary>Apply pending <c>characters</c> migrations at startup. The world server owns that schema.</summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;

    /// <summary>Where the extracted client data lives. Relative paths resolve from the binary.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Message of the day. Newlines become separate lines in the client.</summary>
    public string Motd { get; set; } = "Welcome to WowEmu.";

    /// <summary>Expansion level offered to clients: 0 vanilla, 1 TBC, 2 WotLK.</summary>
    [Range(0, 2)]
    public byte Expansion { get; set; } = 2;

    /// <summary>
    /// Floor on how often the world tick runs, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>MinWorldUpdateTime</c>, and its default is 1. PLAN.md §4.5 records that there
    /// is <b>no fixed 50 ms tick</b> in this tree, whatever the folklore says — the loop runs as
    /// fast as it can and this is only the floor that stops it spinning.
    /// </remarks>
    [Range(1, 1000)]
    public uint MinWorldUpdateTimeMs { get; set; } = 1;

    /// <summary>
    /// How many threads run map updates. Zero runs them inline on the world tick.
    /// </summary>
    /// <remarks>
    /// Defaults to zero because there is one continent's worth of players and inline is simpler to
    /// reason about; raise it when there is enough happening to be worth the barrier. The pool is
    /// dedicated threads, not the thread pool — see <c>MapUpdater</c>.
    /// </remarks>
    [Range(0, 64)]
    public int MapUpdateThreads { get; set; }

    /// <summary>A tick slower than this is logged. PLAN.md §4.5 budgets p99 under 50 ms at 1k players.</summary>
    [Range(1, 60000)]
    public double SlowTickThresholdMs { get; set; } = 50;

    /// <summary>How often logged-in characters are written back, in milliseconds.</summary>
    /// <remarks>
    /// Upstream's <c>PlayerSave.Interval</c> default is 15 minutes. Five is used here because
    /// nothing else about a character is persisted yet, so a lost save costs only position — and
    /// because a save that never visibly happens is a save nobody notices is broken.
    /// </remarks>
    [Range(1000, 3_600_000)]
    public uint PlayerSaveIntervalMs { get; set; } = 300_000;

    /// <summary>How often the tick reports its own health, in milliseconds.</summary>
    [Range(1000, 3_600_000)]
    public uint TickReportIntervalMs { get; set; } = 60_000;
}
