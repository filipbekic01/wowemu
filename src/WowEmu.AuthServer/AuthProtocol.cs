using System.Diagnostics.CodeAnalysis;

namespace WowEmu.AuthServer;

/// <summary>Logon-server commands. The first byte of every packet in both directions.</summary>
public enum AuthCommand : byte
{
    LogonChallenge = 0x00,
    LogonProof = 0x01,
    ReconnectChallenge = 0x02,
    ReconnectProof = 0x03,
    RealmList = 0x10,
}

/// <summary>Result codes the client understands. From <c>AuthCodes.h</c>.</summary>
public enum AuthResult : byte
{
    Success = 0x00,
    FailBanned = 0x03,

    /// <summary>
    /// Also the answer to a <b>wrong password</b>. AzerothCore deliberately never returns
    /// <see cref="FailIncorrectPassword"/>, so a wrong password is indistinguishable from an
    /// unknown account and the protocol leaks no account-existence oracle. Do not "fix" this.
    /// </summary>
    FailUnknownAccount = 0x04,

    FailIncorrectPassword = 0x05,
    FailAlreadyOnline = 0x06,
    FailNoTime = 0x07,
    FailDbBusy = 0x08,
    FailVersionInvalid = 0x09,
    FailVersionUpdate = 0x0A,
    FailInvalidServer = 0x0B,
    FailSuspended = 0x0C,
    FailNoAccess = 0x0D,
    SuccessSurvey = 0x0E,
    FailParentControl = 0x0F,
    FailLockedEnforced = 0x10,
}

/// <summary>Where a connection is in the handshake. Each command is only legal in one state.</summary>
public enum AuthStatus
{
    Challenge,
    LogonProof,
    ReconnectProof,
    Authed,
    Closed,
}

[Flags]
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "\"Realm flags\" is the name the 3.3.5a realm-list packet gives this bitfield.")]
public enum RealmFlags : byte
{
    None = 0x00,
    VersionMismatch = 0x01,
    Offline = 0x02,
    SpecifyBuild = 0x04,
    Recommended = 0x20,
    New = 0x40,
    Full = 0x80,
}

public enum RealmType : byte
{
    Normal = 0,
    Pvp = 1,
    Normal2 = 4,
    Rp = 6,
    RpPvp = 8,
}

/// <summary>Wire constants for the logon protocol.</summary>
public static class AuthProtocol
{
    /// <summary>The only client build this server speaks: 3.3.5a.</summary>
    public const ushort SupportedBuild = 12340;

    /// <summary>Bytes of a logon challenge that must arrive before its length field can be read.</summary>
    public const int ChallengeInitialSize = 4;

    /// <summary>
    /// <c>sizeof(sAuthLogonChallenge_C)</c>, which includes one byte of the account name.
    /// </summary>
    public const int ChallengeStructSize = 35;

    /// <summary>Largest logon challenge accepted; anything larger closes the connection.</summary>
    public const int MaxChallengeSize = ChallengeStructSize + 16;

    /// <summary>
    /// Bytes of the challenge that precede the account name, i.e. the value the client's own
    /// <c>size</c> field must exceed by exactly the account-name length.
    /// </summary>
    public const int ChallengeFixedPayloadSize = ChallengeStructSize - ChallengeInitialSize - 1;

    /// <summary><c>sizeof(sAuthLogonProof_C)</c>.</summary>
    public const int LogonProofSize = 1 + 32 + 20 + 20 + 1 + 1;

    /// <summary><c>sizeof(sAuthReconnectProof_C)</c>.</summary>
    public const int ReconnectProofSize = 1 + 16 + 20 + 20 + 1;

    /// <summary>A realm-list request is a command byte plus four ignored bytes.</summary>
    public const int RealmListRequestSize = 5;

    /// <summary>Length of the random challenge used by the reconnect handshake.</summary>
    public const int ReconnectProofLength = 16;

    /// <summary>
    /// Opaque 16-byte constant appended to both challenge responses. Copy it verbatim.
    /// </summary>
    public static ReadOnlySpan<byte> VersionChallenge =>
    [
        0xBA, 0xA3, 0x1E, 0x99, 0xA0, 0x0B, 0x21, 0x57,
        0xFC, 0x37, 0x3F, 0xB3, 0x69, 0xCD, 0xD2, 0xF1
    ];
}
