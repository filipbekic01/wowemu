namespace WowEmu.Data.Db;

/// <summary>One row of the <c>account</c> table.</summary>
/// <remarks>
/// The SRP6 material is stored as fixed-width binary, not hex text: 32-byte salt, 32-byte verifier,
/// 40-byte session key, exactly as they go on the wire. Upstream stores them as hex strings and pays
/// a parse on every login; there is no reason to inherit that.
/// </remarks>
public sealed class AccountEntity
{
    public uint Id { get; set; }

    /// <summary>
    /// Uppercased with <c>TextTransform.Utf8ToUpperOnlyLatin</c> before it is ever stored or looked
    /// up. The column collation is binary, so the comparison here matches the ordinal comparison the
    /// SRP6 verifier was derived under.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    public byte[] Salt { get; set; } = [];

    public byte[] Verifier { get; set; } = [];

    /// <summary>
    /// The 40-byte key from the last successful logon, or <see langword="null"/> if the account has
    /// never logged in. Persisting it is what lets the world server — a different process — verify
    /// <c>CMSG_AUTH_SESSION</c>, and what lets the reconnect handshake work on a fresh connection.
    /// </summary>
    public byte[]? SessionKey { get; set; }

    public byte SecurityLevel { get; set; }

    /// <summary>
    /// The furthest expansion this account has bought. 0 vanilla, 1 TBC, 2 WotLK.
    /// </summary>
    /// <remarks>
    /// Per account, not per realm — it is what a player paid for, and it gates which races and
    /// classes they may create. Defaulted to WotLK because that is what this server serves.
    /// </remarks>
    public byte Expansion { get; set; } = 2;

    /// <summary>Account flags echoed back in the logon proof. Zero for a normal account.</summary>
    public uint Flags { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public string? LastIp { get; set; }

    public ushort? LastBuild { get; set; }
}

/// <summary>One row of the <c>realmlist</c> table: a realm advertised to clients after login.</summary>
public sealed class RealmEntity
{
    /// <summary>Realm id. Must match the world server's configured realm.</summary>
    public byte Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Host the client is told to connect to. Sent to the client as <c>address:port</c>.</summary>
    public string Address { get; set; } = string.Empty;

    public ushort Port { get; set; }

    /// <summary>Maps to <c>RealmType</c>; kept as a raw byte so the schema owns no game enum.</summary>
    public byte Type { get; set; }

    /// <summary>Maps to <c>RealmFlags</c>.</summary>
    public byte Flags { get; set; }

    public float PopulationLevel { get; set; }

    public byte Timezone { get; set; }

    public byte AllowedSecurityLevel { get; set; }

    /// <summary>Client build this realm serves. Mismatching clients see it as offline.</summary>
    public ushort Build { get; set; }
}

/// <summary>
/// One row of the <c>build_info</c> table: a client build this server accepts.
/// </summary>
/// <remarks>
/// Build gating is deliberately data-driven, exactly as upstream. An empty table rejects every
/// login with <c>WOW_FAIL_VERSION_INVALID</c> — which looks like a protocol bug and is not one.
/// </remarks>
public sealed class BuildInfoEntity
{
    public ushort Build { get; set; }

    public byte MajorVersion { get; set; }

    public byte MinorVersion { get; set; }

    public byte BugfixVersion { get; set; }

    /// <summary>The trailing letter in "3.3.5a", or <see langword="null"/> if there isn't one.</summary>
    public string? HotfixLetter { get; set; }
}
