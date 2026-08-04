namespace WowEmu.Data.Db;

/// <summary>
/// An account as the logon server sees it: an immutable snapshot, detached from the change tracker.
/// </summary>
/// <remarks>
/// Repositories hand these out instead of <see cref="AccountEntity"/> so that a live EF entity never
/// ends up parked in a long-running connection's state, where it would outlive its context.
/// </remarks>
public sealed record AuthAccount(
    uint Id,
    string Username,
    byte[] Salt,
    byte[] Verifier,
    byte[]? SessionKey,
    byte SecurityLevel,
    uint Flags);

/// <summary>What the account CLI prints. Deliberately carries no secrets.</summary>
public sealed record AuthAccountSummary(
    uint Id,
    string Username,
    byte SecurityLevel,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    string? LastIp,
    bool HasSessionKey);

/// <summary>A realm row, as stored. The logon server turns this into its own wire-facing type.</summary>
public sealed record RealmRegistration(
    byte Id,
    string Name,
    string Address,
    ushort Port,
    byte Type,
    byte Flags,
    float PopulationLevel,
    byte Timezone,
    byte AllowedSecurityLevel,
    ushort Build);
