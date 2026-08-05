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

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information,
        Message = "Applied {Count} pending characters-database migration(s)")]
    public static partial void MigrationsApplied(ILogger logger, int count);

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

    [LoggerMessage(EventId = 2108, Level = LogLevel.Warning,
        Message = "Opcode {Opcode} is not in the opcode table ({Address}); closing")]
    public static partial void UnknownOpcode(ILogger logger, Opcode opcode, string address);

    [LoggerMessage(EventId = 2109, Level = LogLevel.Debug,
        Message = "Dropped {Opcode} from {Address}: needs {Required}, session is {Current}")]
    public static partial void OpcodeNotAllowed(
        ILogger logger, Opcode opcode, SessionStatus required, SessionStatus current, string address);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Debug,
        Message = "Sent {Count} character(s) to {Address}")]
    public static partial void CharacterListSent(ILogger logger, int count, string address);

    // ------------------------------------------------------------------ characters

    [LoggerMessage(EventId = 2300, Level = LogLevel.Information,
        Message = "Created character '{Name}' (guid {CharacterId}) for '{Account}' from {Address}")]
    public static partial void CharacterCreated(
        ILogger logger, string name, uint characterId, string account, string address);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information,
        Message = "Deleted character {CharacterId} for '{Account}' from {Address}")]
    public static partial void CharacterDeleted(ILogger logger, uint characterId, string account, string address);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Warning,
        Message = "Rejected character creation: race {Race}, class {Class}, gender {Gender} is not a valid combination ({Address})")]
    public static partial void InvalidCharacterCreate(
        ILogger logger, byte race, byte @class, byte gender, string address);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Information,
        Message = "Loaded {Count} race/class start positions from the world database")]
    public static partial void CreateInfoLoaded(ILogger logger, int count);

    [LoggerMessage(EventId = 2304, Level = LogLevel.Information,
        Message = "'{Name}' entered the world on map {MapId} at ({X:F1}, {Y:F1}) from {Address}")]
    public static partial void PlayerEnteredWorld(
        ILogger logger, string name, uint mapId, float x, float y, string address);

    [LoggerMessage(EventId = 2305, Level = LogLevel.Warning,
        Message = "Refused login for character {CharacterId}: {Reason} ({Address})")]
    public static partial void LoginRejected(ILogger logger, uint characterId, string reason, string address);

    [LoggerMessage(EventId = 2306, Level = LogLevel.Information,
        Message = "Loaded {Races} races, {Classes} classes, {Maps} maps, {LevelStats} level stat rows")]
    public static partial void ContentLoaded(ILogger logger, int races, int classes, int maps, int levelStats);

    [LoggerMessage(EventId = 2307, Level = LogLevel.Information,
        Message = "'{Name}' left the world ({Address})")]
    public static partial void PlayerLeftWorld(ILogger logger, string name, string address);

    [LoggerMessage(EventId = 2308, Level = LogLevel.Debug,
        Message = "Saved '{Name}' at ({X:F1}, {Y:F1})")]
    public static partial void PlayerSaved(ILogger logger, string name, float x, float y);

    [LoggerMessage(EventId = 2309, Level = LogLevel.Debug,
        Message = "'{Name}' moved into area {AreaId}")]
    public static partial void ZoneChanged(ILogger logger, string name, ushort areaId);

    [LoggerMessage(EventId = 2310, Level = LogLevel.Information,
        Message = "Terrain available: {TileCount} map tiles in {Directory}")]
    public static partial void TerrainAvailable(ILogger logger, int tileCount, string directory);

    [LoggerMessage(EventId = 2311, Level = LogLevel.Warning,
        Message = "No map tiles at {Directory} — the server cannot tell where the ground is")]
    public static partial void TerrainMissing(ILogger logger, string directory);

    [LoggerMessage(EventId = 2312, Level = LogLevel.Debug,
        Message = "'{Target}' became visible to '{Viewer}'")]
    public static partial void ObjectBecameVisible(ILogger logger, string target, string viewer);

    [LoggerMessage(EventId = 2313, Level = LogLevel.Warning,
        Message = "Refused movement from '{Name}': {Reason} ({Detail}) — {Address}")]
    public static partial void MovementRejected(
        ILogger logger, string name, string reason, string detail, string address);

    [LoggerMessage(EventId = 2314, Level = LogLevel.Information,
        Message = "Loaded {Templates} creature templates, {Models} models and {Spawns} spawns "
                + "across {Maps} maps in {ElapsedMs:F0} ms")]
    public static partial void CreatureContentLoaded(
        ILogger logger, int templates, int models, int spawns, int maps, double elapsedMs);

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
