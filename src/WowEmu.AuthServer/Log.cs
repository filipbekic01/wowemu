using System.Net;
using Microsoft.Extensions.Logging;

namespace WowEmu.AuthServer;

/// <summary>
/// Source-generated log messages.
/// </summary>
/// <remarks>
/// Using <see cref="LoggerMessageAttribute"/> rather than the <c>LogInformation(...)</c> extension
/// methods keeps the message templates in one place, avoids boxing every argument into a
/// <c>params object[]</c>, and skips argument evaluation entirely when the level is disabled — which
/// matters for the per-packet Debug messages on the session path.
/// </remarks>
internal static partial class Log
{
    // ------------------------------------------------------------------ startup

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Applied {Count} pending database migration(s)")]
    public static partial void MigrationsApplied(ILogger logger, int count);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "Database schema is up to date")]
    public static partial void SchemaUpToDate(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "No accounts exist. Create one with: dotnet run --project tools/WowEmu.AccountCli -- account create <name> <password>")]
    public static partial void NoAccountsConfigured(ILogger logger);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "Logon server listening on {Address}:{Port}")]
    public static partial void Listening(ILogger logger, IPAddress address, int port);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "{AccountCount} account(s) registered, {RealmCount} realm(s) advertised")]
    public static partial void ServerSummary(ILogger logger, int accountCount, int realmCount);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
        Message = "Point your 3.3.5a client's realmlist.wtf at this address to connect")]
    public static partial void RealmlistHint(ILogger logger);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information,
        Message = "Logon server shutting down")]
    public static partial void ShuttingDown(ILogger logger);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug,
        Message = "Loaded {Count} realm(s) from the database")]
    public static partial void RealmsLoaded(ILogger logger, int count);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning,
        Message = "Could not refresh the realm list; continuing with the cached one")]
    public static partial void RealmRefreshFailed(ILogger logger, Exception exception);

    // ------------------------------------------------------------------ connections

    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug,
        Message = "Connection from {Address}")]
    public static partial void ClientConnected(ILogger logger, EndPoint? address);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error,
        Message = "Unhandled error serving {Address}")]
    public static partial void SessionFailed(ILogger logger, Exception exception, EndPoint? address);

    // ------------------------------------------------------------------ protocol

    [LoggerMessage(EventId = 1200, Level = LogLevel.Debug,
        Message = "Unknown auth command 0x{Command:X2} from {Address}; discarding buffer")]
    public static partial void UnknownCommand(ILogger logger, byte command, string address);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning,
        Message = "Oversized logon challenge ({Size} bytes) from {Address}; closing")]
    public static partial void OversizedChallenge(ILogger logger, int size, string address);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning,
        Message = "Command {Command} from {Address} not allowed in state {State}; closing")]
    public static partial void CommandOutOfOrder(ILogger logger, AuthCommand command, string address, AuthStatus state);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning,
        Message = "Malformed challenge from {Address}: length field {Size} does not match name length {NameLength}")]
    public static partial void MalformedChallenge(ILogger logger, string address, ushort size, byte nameLength);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Information,
        Message = "Rejecting unsupported client build {Build} from {Address}")]
    public static partial void UnsupportedBuild(ILogger logger, ushort build, string address);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Information,
        Message = "Unknown account '{Login}' from {Address}")]
    public static partial void UnknownAccount(ILogger logger, string login, string address);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Debug,
        Message = "Challenge sent to '{Login}' (locale {Locale}, os {Os}) from {Address}")]
    public static partial void ChallengeSent(ILogger logger, string login, string locale, string os, string address);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Information,
        Message = "Bad password for '{Login}' from {Address}")]
    public static partial void BadPassword(ILogger logger, string login, string address);

    [LoggerMessage(EventId = 1208, Level = LogLevel.Information,
        Message = "Account '{Login}' authenticated from {Address}")]
    public static partial void Authenticated(ILogger logger, string login, string address);

    [LoggerMessage(EventId = 1209, Level = LogLevel.Warning,
        Message = "Invalid reconnect proof for '{Login}' from {Address}")]
    public static partial void BadReconnectProof(ILogger logger, string login, string address);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Information,
        Message = "Account '{Login}' reconnected from {Address}")]
    public static partial void Reconnected(ILogger logger, string login, string address);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Debug,
        Message = "Sent {Count} realm(s) to {Address}")]
    public static partial void RealmListSent(ILogger logger, int count, string address);
}
