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

    [LoggerMessage(EventId = 2315, Level = LogLevel.Error,
        Message = "Handler for {Opcode} threw ({Address})")]
    public static partial void PacketHandlerFailed(
        ILogger logger, Exception exception, Opcode opcode, string address);

    [LoggerMessage(EventId = 2316, Level = LogLevel.Error,
        Message = "Deferred work for a session threw ({Address})")]
    public static partial void DeferredWorkFailed(ILogger logger, Exception exception, string address);

    [LoggerMessage(EventId = 2317, Level = LogLevel.Information,
        Message = "Loaded {Templates} gameobject templates and {Spawns} spawns across {Maps} maps")]
    public static partial void GameObjectContentLoaded(
        ILogger logger, int templates, int spawns, int maps);

    [LoggerMessage(EventId = 2318, Level = LogLevel.Debug,
        Message = "'{Name}' started attacking '{Target}' — {Address}")]
    public static partial void AttackStarted(ILogger logger, string name, string target, string address);

    [LoggerMessage(EventId = 2331, Level = LogLevel.Information,
        Message = "Loaded {Rows} creature loot rows across {Ids} ids, {RefRows} reference rows across {RefIds}")]
    public static partial void LootTemplatesLoaded(
        ILogger logger, int rows, int ids, int refRows, int refIds);

    [LoggerMessage(EventId = 2330, Level = LogLevel.Information,
        Message = "Item guids continue from {Highest}")]
    public static partial void ItemGuidsSeeded(ILogger logger, uint highest);

    [LoggerMessage(EventId = 2327, Level = LogLevel.Debug,
        Message = "'{Name}' starts with {Items} item(s)")]
    public static partial void StartingGearGiven(ILogger logger, string name, int items);

    [LoggerMessage(EventId = 2328, Level = LogLevel.Warning,
        Message = "'{Name}' was created with no starting gear: {Reason}")]
    public static partial void StartingGearSkipped(ILogger logger, string name, string reason);

    [LoggerMessage(EventId = 2329, Level = LogLevel.Warning,
        Message = "Dropped {Rows} inventory row(s) for '{Name}' — no such item_template")]
    public static partial void InventoryRowsDropped(ILogger logger, string name, int rows);

    [LoggerMessage(EventId = 2326, Level = LogLevel.Information,
        Message = "Loaded {Templates} item templates")]
    public static partial void ItemTemplatesLoaded(ILogger logger, int templates);

    [LoggerMessage(EventId = 2325, Level = LogLevel.Information,
        Message = "Loaded {Links} graveyard links across {Zones} zones, {Locations} safe locations")]
    public static partial void GraveyardsLoaded(ILogger logger, int links, int zones, int locations);

    [LoggerMessage(EventId = 2323, Level = LogLevel.Debug,
        Message = "'{Name}' released its spirit — {Address}")]
    public static partial void PlayerReleased(ILogger logger, string name, string address);

    [LoggerMessage(EventId = 2324, Level = LogLevel.Debug,
        Message = "'{Name}' resurrected at its corpse — {Address}")]
    public static partial void PlayerResurrected(ILogger logger, string name, string address);

    [LoggerMessage(EventId = 2321, Level = LogLevel.Information,
        Message = "Loaded {Rows} experience-per-level rows, to level {MaxLevel}")]
    public static partial void ExperienceTableLoaded(ILogger logger, int rows, byte maxLevel);

    [LoggerMessage(EventId = 2322, Level = LogLevel.Warning,
        Message = "No rows in player_xp_for_level — nobody will gain a level")]
    public static partial void ExperienceTableMissing(ILogger logger);

    [LoggerMessage(EventId = 2320, Level = LogLevel.Debug,
        Message = "'{Name}' cast {Spell} at {Target} — {Address}")]
    public static partial void SpellCast(
        ILogger logger, string name, string spell, string target, string address);

    [LoggerMessage(EventId = 2319, Level = LogLevel.Information,
        Message = "Loaded {Spells} spells, {CastTimes} cast times, {Ranges} ranges and {Durations} durations")]
    public static partial void SpellDataLoaded(
        ILogger logger, int spells, int castTimes, int ranges, int durations);

    // ------------------------------------------------------------------ world tick

    [LoggerMessage(EventId = 2400, Level = LogLevel.Information,
        Message = "World tick running at a {MinUpdateMs} ms floor over {MapCount} map(s)")]
    public static partial void TickStarted(ILogger logger, uint minUpdateMs, int mapCount);

    [LoggerMessage(EventId = 2401, Level = LogLevel.Information,
        Message = "World tick stopped after {Ticks} tick(s); longest was {LongestMs:F1} ms")]
    public static partial void TickStopped(ILogger logger, long ticks, double longestMs);

    [LoggerMessage(EventId = 2402, Level = LogLevel.Warning,
        Message = "Tick took {ElapsedMs:F1} ms for {DiffMs} ms of game time with {Sessions} session(s)")]
    public static partial void SlowTick(ILogger logger, double elapsedMs, uint diffMs, int sessions);

    [LoggerMessage(EventId = 2403, Level = LogLevel.Error,
        Message = "Work posted to the world tick threw")]
    public static partial void TickWorkFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2404, Level = LogLevel.Warning,
        Message = "Tick drain hit its budget; {Deferred} item(s) held over")]
    public static partial void TickWorkDeferred(ILogger logger, int deferred);

    [LoggerMessage(EventId = 2405, Level = LogLevel.Debug,
        Message = "Periodic save: {Count} character(s)")]
    public static partial void PeriodicSave(ILogger logger, int count);

    [LoggerMessage(EventId = 2406, Level = LogLevel.Information,
        Message = "{Ticks} ticks | {Sessions} session(s) | {Maps} map(s) | worst tick {LongestMs:F1} ms")]
    public static partial void TickReport(
        ILogger logger, long ticks, int sessions, int maps, double longestMs);

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
