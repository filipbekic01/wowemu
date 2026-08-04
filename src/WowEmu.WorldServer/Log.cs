using System.Net;
using Microsoft.Extensions.Logging;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>Source-generated log messages. Same rationale as the logon server's.</summary>
internal static partial class Log
{
    // ------------------------------------------------------------------ startup

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "World server listening on {Address}:{Port} as realm {RealmId}")]
    public static partial void Listening(ILogger logger, IPAddress address, int port, byte realmId);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "World server shutting down")]
    public static partial void ShuttingDown(ILogger logger);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Debug,
        Message = "Connection from {Address}")]
    public static partial void ClientConnected(ILogger logger, EndPoint? address);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error,
        Message = "Unhandled error serving {Address}")]
    public static partial void SessionFailed(ILogger logger, Exception exception, EndPoint? address);

    // ------------------------------------------------------------------ handshake

    [LoggerMessage(EventId = 2100, Level = LogLevel.Warning,
        Message = "Malformed CMSG_AUTH_SESSION from {Address}; closing")]
    public static partial void MalformedAuthSession(ILogger logger, string address);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information,
        Message = "No session key for account '{Account}' from {Address} — did it log in through the auth server?")]
    public static partial void UnknownAccount(ILogger logger, string account, string address);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning,
        Message = "Digest mismatch for '{Account}' from {Address}; closing")]
    public static partial void BadDigest(ILogger logger, string account, string address);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning,
        Message = "Client asked for realm {Requested} but this server is realm {Actual} ({Address})")]
    public static partial void WrongRealm(ILogger logger, uint requested, byte actual, string address);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Information,
        Message = "Account '{Account}' authenticated (build {Build}) from {Address}")]
    public static partial void Authenticated(ILogger logger, string account, uint build, string address);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Warning,
        Message = "Opcode {Opcode} from {Address} before authentication; closing")]
    public static partial void PacketBeforeAuth(ILogger logger, Opcode opcode, string address);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Debug,
        Message = "No handler for {Opcode} from {Address}")]
    public static partial void UnhandledOpcode(ILogger logger, Opcode opcode, string address);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Debug,
        Message = "Sent {Count} character(s) to {Address}")]
    public static partial void CharacterListSent(ILogger logger, int count, string address);

    // ------------------------------------------------------------------ addons

    [LoggerMessage(EventId = 2200, Level = LogLevel.Warning,
        Message = "Addon manifest claims {Size} bytes uncompressed, over the cap; ignoring it")]
    public static partial void AddonInfoTooLarge(ILogger logger, uint size);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Debug,
        Message = "Addon manifest could not be decompressed; ignoring it")]
    public static partial void AddonInfoUnreadable(ILogger logger);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Debug,
        Message = "Addon manifest ended after {Read} of {Claimed} entries")]
    public static partial void AddonInfoTruncated(ILogger logger, uint read, uint claimed);
}
