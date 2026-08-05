using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;
using WowEmu.Network;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>
/// One authenticated (or authenticating) client session on the world server.
/// </summary>
/// <remarks>
/// Port of the session half of <c>WorldSocket</c> plus the handful of handlers the client needs
/// before it will show the character-selection screen.
/// <para>
/// Dispatch is gated by the generated opcode table: an opcode's required session status and its
/// processing class are checked before any handler runs, so an opcode upstream never accepts from a
/// client cannot reach one here either.
/// </para>
/// </remarks>
public sealed class WorldSession(
    WorldConnection connection,
    IAccountRepository accounts,
    ICharacterRepository characters,
    PlayerCreateInfoStore createInfo,
    WorldContent world,
    MapManager maps,
    WorldServerOptions options,
    ILogger logger) : IPlayerConnection
{
    /// <summary>Result codes from <c>SharedDefines.h</c>. Only the ones this phase can produce.</summary>
    private const byte AuthOk = 0x0C;
    private const byte AuthFailed = 0x0D;
    private const byte AuthUnknownAccount = 0x15;

    /// <summary>Account data slots and the subset that is account-wide rather than per character.</summary>
    private const int AccountDataTypeCount = 8;
    private const uint GlobalCacheMask = 0x15;

    /// <summary>Tutorial flag words the client expects, whether or not any are set.</summary>
    private const int TutorialValueCount = 8;

    /// <summary>Character create/delete results from <c>SharedDefines.h</c>.</summary>
    private const byte CharCreateSuccess = 0x2F;
    private const byte CharCreateError = 0x30;
    private const byte CharCreateFailed = 0x31;
    private const byte CharCreateNameInUse = 0x32;
    private const byte CharCreateAccountLimit = 0x36;
    private const byte CharNameInvalidCharacter = 0x5C;
    private const byte CharDeleteSuccess = 0x47;
    private const byte CharDeleteFailed = 0x48;

    /// <summary>Retail's per-realm cap, and the client's own assumption.</summary>
    private const int MaxCharactersPerAccount = 10;

    private readonly byte[] _authSeed = RandomNumberGenerator.GetBytes(4);

    private AuthAccount? _account;
    private Player? _player;
    private Map? _map;
    private uint _lastMovementMs;
    private bool _authenticated;

    /// <summary>
    /// How far through login this session is, which decides what opcodes it may send.
    /// </summary>
    /// <remarks>
    /// Starts at <see cref="SessionStatus.Authed"/> and becomes <see cref="SessionStatus.LoggedIn"/>
    /// when a character enters the world, which is what starts admitting gameplay opcodes.
    /// <see cref="SessionStatus.Transfer"/> is never entered yet — nothing moves between maps.
    /// </remarks>
    public SessionStatus Status { get; private set; } = SessionStatus.Authed;

    /// <summary>Sends the challenge that opens the handshake, then pumps packets.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await SendAuthChallengeAsync(cancellationToken).ConfigureAwait(false);
        await connection.RunAsync(HandleAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HandleAsync(Opcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // CMSG_AUTH_SESSION and CMSG_PING are handled here at the socket layer rather than through
        // a session dispatch table, exactly as upstream does — the first because no session exists
        // yet, the second because it must answer even when the session is busy.
        switch (opcode)
        {
            case Opcode.CMSG_AUTH_SESSION:
                return await HandleAuthSessionAsync(payload, cancellationToken).ConfigureAwait(false);

            case Opcode.CMSG_PING:
                return await HandlePingAsync(payload, cancellationToken).ConfigureAwait(false);

            case Opcode.CMSG_KEEP_ALIVE:
                return true;
        }

        if (!_authenticated)
        {
            Log.PacketBeforeAuth(logger, opcode, connection.RemoteAddress);
            return false;
        }

        // The generated table decides what is legal before any handler sees the packet. An opcode
        // upstream never accepts from a client, or one that needs a player in the world when there
        // isn't one, is dropped — but the session survives, because upstream tolerates it too and a
        // client that sends one is confused rather than hostile.
        if (!OpcodeTable.TryGet(opcode, out OpcodeInfo? info))
        {
            Log.UnknownOpcode(logger, opcode, connection.RemoteAddress);
            return false;
        }

        if (!OpcodeTable.IsAllowedFrom(opcode, Status))
        {
            Log.OpcodeNotAllowed(logger, opcode, info.Value.Status, Status, connection.RemoteAddress);
            return true;
        }

        switch (opcode)
        {
            case Opcode.CMSG_READY_FOR_ACCOUNT_DATA_TIMES:
                await SendAccountDataTimesAsync(GlobalCacheMask, cancellationToken).ConfigureAwait(false);
                return true;

            case Opcode.CMSG_CHAR_ENUM:
                await SendCharacterListAsync(cancellationToken).ConfigureAwait(false);
                return true;

            case Opcode.CMSG_CHAR_CREATE:
                await HandleCharacterCreateAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;

            case Opcode.CMSG_CHAR_DELETE:
                await HandleCharacterDeleteAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;

            case Opcode.CMSG_PLAYER_LOGIN:
                await HandlePlayerLoginAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;

            case Opcode.CMSG_REALM_SPLIT:
                await HandleRealmSplitAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;

            case Opcode.CMSG_LOGOUT_REQUEST:
                await HandleLogoutRequestAsync(cancellationToken).ConfigureAwait(false);
                return true;

            case Opcode.CMSG_LOGOUT_CANCEL:
                await HandleLogoutCancelAsync(cancellationToken).ConfigureAwait(false);
                return true;

            default:
                // Every movement opcode routes to one handler, exactly as upstream does — the
                // opcode says what the client thinks it is doing, but the payload is identical.
                if (info.Value.UpstreamHandler == "HandleMovementOpcodes")
                {
                    await HandleMovementAsync(opcode, payload, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                Log.UnhandledOpcode(logger, opcode, connection.RemoteAddress);
                return true;
        }
    }

    // ------------------------------------------------------------------ handshake

    private async Task SendAuthChallengeAsync(CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_AUTH_CHALLENGE, 40);

        packet.Body.WriteUInt32(1);
        packet.Body.WriteBytes(_authSeed);

        // 32 bytes upstream labels "new encryption seeds". The 3.3.5a client reads and discards
        // them; they matter to later expansions. Sent because the client expects 40 bytes.
        packet.Body.WriteBytes(RandomNumberGenerator.GetBytes(32));

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HandleAuthSessionAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!TryParseAuthSession(payload.Span, out AuthSessionRequest request))
        {
            Log.MalformedAuthSession(logger, connection.RemoteAddress);
            return false;
        }

        _account = await accounts.FindAsync(request.Account, cancellationToken).ConfigureAwait(false);

        if (_account?.SessionKey is null)
        {
            // No key means the account never completed a logon, so there is nothing to encrypt
            // with — the client cannot read this response, but upstream sends it anyway.
            Log.UnknownAccount(logger, request.Account, connection.RemoteAddress);
            await SendAuthResponseErrorAsync(AuthUnknownAccount, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Encryption goes on BEFORE the digest is checked. The client switched its own crypt on the
        // moment it sent this packet, so a plaintext rejection would be unreadable and the client
        // would sit at a hang instead of showing an error. Verifying first is the "obvious"
        // ordering and it is wrong.
        connection.EnableEncryption(_account.SessionKey);

        if (request.RealmId != options.RealmId)
        {
            Log.WrongRealm(logger, request.RealmId, options.RealmId, connection.RemoteAddress);
            await SendAuthResponseErrorAsync(AuthFailed, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!VerifyDigest(request, _account.SessionKey))
        {
            Log.BadDigest(logger, _account.Username, connection.RemoteAddress);
            await SendAuthResponseErrorAsync(AuthFailed, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _authenticated = true;
        Log.Authenticated(logger, _account.Username, request.Build, connection.RemoteAddress);

        await SendAuthResponseAsync(cancellationToken).ConfigureAwait(false);
        await SendAddonInfoAsync(request.AddonInfo, cancellationToken).ConfigureAwait(false);
        await SendClientCacheVersionAsync(cancellationToken).ConfigureAwait(false);
        await SendTutorialFlagsAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Recomputes the client's proof of possession of the session key.
    /// </summary>
    /// <remarks>
    /// <c>SHA1(account || {0,0,0,0} || clientChallenge || serverSeed || sessionKey)</c>. The four
    /// zero bytes are not padding to be tidied away — they are part of the hashed message, and
    /// dropping them produces a digest that never matches.
    /// </remarks>
    private bool VerifyDigest(AuthSessionRequest request, byte[] sessionKey)
    {
        Span<byte> expected = stackalloc byte[20];

        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(request.Account));
            hash.AppendData([0, 0, 0, 0]);
            hash.AppendData(request.LocalChallenge);
            hash.AppendData(_authSeed);
            hash.AppendData(sessionKey);
            hash.GetHashAndReset(expected);
        }

        return CryptographicOperations.FixedTimeEquals(expected, request.Digest);
    }

    private async Task SendAuthResponseAsync(CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_AUTH_RESPONSE, 11);

        packet.Body.WriteUInt8(AuthOk);
        packet.Body.WriteUInt32(0);                  // billing time remaining
        packet.Body.WriteUInt8(0);                   // billing plan flags
        packet.Body.WriteUInt32(0);                  // billing time rested
        packet.Body.WriteUInt8(options.Expansion);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAuthResponseErrorAsync(byte code, CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_AUTH_RESPONSE, 1);
        packet.Body.WriteUInt8(code);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ post-auth chatter

    /// <summary>
    /// Answers the client's addon manifest.
    /// </summary>
    /// <remarks>
    /// The request is a zlib stream of the client's enabled addons. Each one gets a reply saying
    /// "enabled"; an addon whose CRC is not the standard Blizzard value additionally gets the
    /// 256-byte public key, which is opaque data copied byte for byte.
    /// </remarks>
    private async Task SendAddonInfoAsync(ReadOnlyMemory<byte> compressed, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClientAddon> addons = AddonInfo.Parse(compressed.Span, logger);

        ServerPacket packet = new(Opcode.SMSG_ADDON_INFO, 64);

        foreach (ClientAddon addon in addons)
        {
            packet.Body.WriteUInt8(2);           // state: enabled
            packet.Body.WriteUInt8(1);           // uses public key or CRC

            bool needsKey = addon.Crc != AddonInfo.StandardCrc;
            packet.Body.WriteUInt8((byte)(needsKey ? 1 : 0));

            if (needsKey)
            {
                packet.Body.WriteBytes(AddonInfo.PublicKey);
            }

            packet.Body.WriteUInt32(0);          // meaning unknown upstream too
            packet.Body.WriteUInt8(0);           // no URL string follows
        }

        packet.Body.WriteUInt32(0);              // banned addon count

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendClientCacheVersionAsync(CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_CLIENTCACHE_VERSION, 4);
        packet.Body.WriteUInt32(options.ClientCacheVersion);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendTutorialFlagsAsync(CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_TUTORIAL_FLAGS, TutorialValueCount * 4);

        for (int i = 0; i < TutorialValueCount; i++)
        {
            packet.Body.WriteUInt32(0);
        }

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAccountDataTimesAsync(uint mask, CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_ACCOUNT_DATA_TIMES, 4 + 1 + 4 + (AccountDataTypeCount * 4));

        packet.Body.WriteUInt32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        packet.Body.WriteUInt8(1);
        packet.Body.WriteUInt32(mask);

        for (int i = 0; i < AccountDataTypeCount; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                packet.Body.WriteUInt32(0);      // never written, so no timestamp yet
            }
        }

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers <c>CMSG_CHAR_ENUM</c> from the characters database.
    /// </summary>
    /// <remarks>
    /// Built from real rows in the characters database. Equipment is not included — every slot is
    /// written as empty, because no phase has items yet.
    /// </remarks>
    private async Task SendCharacterListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CharacterSummary> roster = _account is null
            ? []
            : await characters.ListForAccountAsync(_account.Id, cancellationToken).ConfigureAwait(false);

        ServerPacket packet = new(Opcode.SMSG_CHAR_ENUM, 1 + (roster.Count * CharacterList.MaxBytesPerCharacter));
        CharacterList.Write(packet.Body, roster);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);

        Log.CharacterListSent(logger, roster.Count, connection.RemoteAddress);
    }

    /// <summary>
    /// Creates a character.
    /// </summary>
    /// <remarks>
    /// Every check the client already performs is repeated here, because the client is not the
    /// authority: it is a program on someone else's machine that can be told to send anything.
    /// <para>
    /// Race/class validity comes from <c>playercreateinfo</c> rather than a hard-coded table —
    /// a pair with no starting position is a pair that cannot exist, so one lookup answers both
    /// questions.
    /// </para>
    /// </remarks>
    private async Task HandleCharacterCreateAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadCString(out string rawName) ||
            !reader.TryReadUInt8(out byte race) ||
            !reader.TryReadUInt8(out byte characterClass) ||
            !reader.TryReadUInt8(out byte gender) ||
            !reader.TryReadUInt8(out byte skin) ||
            !reader.TryReadUInt8(out byte face) ||
            !reader.TryReadUInt8(out byte hairStyle) ||
            !reader.TryReadUInt8(out byte hairColor) ||
            !reader.TryReadUInt8(out byte facialHair))
        {
            await SendCharacterCreateResultAsync(CharCreateError, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!CharacterName.TryNormalize(rawName, out string name))
        {
            await SendCharacterCreateResultAsync(CharNameInvalidCharacter, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (gender > 1 || !createInfo.TryGet(race, characterClass, out PlayerCreateInfo start))
        {
            Log.InvalidCharacterCreate(logger, race, characterClass, gender, connection.RemoteAddress);
            await SendCharacterCreateResultAsync(CharCreateFailed, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await characters.CountForAccountAsync(_account.Id, cancellationToken).ConfigureAwait(false)
            >= MaxCharactersPerAccount)
        {
            await SendCharacterCreateResultAsync(CharCreateAccountLimit, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await characters.NameExistsAsync(name, cancellationToken).ConfigureAwait(false))
        {
            await SendCharacterCreateResultAsync(CharCreateNameInUse, cancellationToken).ConfigureAwait(false);
            return;
        }

        CharacterEntity character = new()
        {
            AccountId = _account.Id,
            Name = name,
            Race = race,
            Class = characterClass,
            Gender = gender,
            Skin = skin,
            Face = face,
            HairStyle = hairStyle,
            HairColor = hairColor,
            FacialStyle = facialHair,
            Level = 1,
            Map = start.Map,
            Zone = start.Zone,
            PositionX = start.PositionX,
            PositionY = start.PositionY,
            PositionZ = start.PositionZ,
            Orientation = start.Orientation,
            AtLoginFlags = CharacterList.AtLoginFirst,
            CreatedAt = DateTime.UtcNow,
        };

        uint? id = await characters.CreateAsync(character, cancellationToken).ConfigureAwait(false);

        if (id is null)
        {
            // Lost the race on the unique index — someone else took the name in between.
            await SendCharacterCreateResultAsync(CharCreateNameInUse, cancellationToken).ConfigureAwait(false);
            return;
        }

        Log.CharacterCreated(logger, name, id.Value, _account.Username, connection.RemoteAddress);
        await SendCharacterCreateResultAsync(CharCreateSuccess, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a character the account owns.</summary>
    private async Task HandleCharacterDeleteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid))
        {
            await SendCharacterDeleteResultAsync(CharDeleteFailed, cancellationToken).ConfigureAwait(false);
            return;
        }

        ObjectGuid guid = new(rawGuid);

        // Ownership is verified in the delete itself, so a client asking to delete someone else's
        // character simply finds nothing to delete.
        bool deleted = await characters
            .DeleteAsync(_account.Id, guid.Counter, cancellationToken)
            .ConfigureAwait(false);

        if (deleted)
        {
            Log.CharacterDeleted(logger, guid.Counter, _account.Username, connection.RemoteAddress);
        }

        await SendCharacterDeleteResultAsync(deleted ? CharDeleteSuccess : CharDeleteFailed, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendCharacterCreateResultAsync(byte result, CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_CHAR_CREATE, 1);
        packet.Body.WriteUInt8(result);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCharacterDeleteResultAsync(byte result, CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_CHAR_DELETE, 1);
        packet.Body.WriteUInt8(result);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records where the client says it is.
    /// </summary>
    /// <remarks>
    /// All 27 movement opcodes carry a packed guid and the same <see cref="MovementInfo"/> block.
    /// <para>
    /// The claim is validated before it is applied — coordinates, teleport distance, speed against
    /// a server-measured interval, and flag sanity. What is <i>not</i> checked is height against
    /// terrain and swimming against liquid: both need vmaps, and approximating them would reject
    /// honest players standing on bridges. See <see cref="MovementValidator"/>.
    /// </para>
    /// </remarks>
    private async Task HandleMovementAsync(
        Opcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadPackedGuid(out ObjectGuid mover) || mover != _player.Guid)
        {
            // A client may only move its own character.
            return;
        }

        // Parsed into a scratch object, not the player's own: a rejected packet must leave the
        // player's state exactly as it was.
        MovementInfo claimed = new();

        if (!claimed.TryReadFrom(ref reader))
        {
            return;
        }

        uint now = MsTime.Now;
        uint elapsed = _lastMovementMs == 0 ? 0 : MsTime.Diff(_lastMovementMs, now);

        MovementVerdict verdict = MovementValidator.Validate(_player.Position, claimed, elapsed);

        if (!verdict.Accepted)
        {
            Log.MovementRejected(
                logger, _player.Name, verdict.Rejection.ToString(), verdict.Detail ?? "", connection.RemoteAddress);

            // The client is told where the server thinks it is, so an honest client that drifted
            // snaps back instead of desynchronising silently.
            await SendKnownPositionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _lastMovementMs = now;
        _player.Movement.CopyFrom(claimed);

        // The map owns position: moving cells is what keeps visibility queries correct.
        await _map.RelocateAsync(_player, _player.Movement.Position, cancellationToken).ConfigureAwait(false);

        // Cheap enough to do per packet: the tile is already loaded and the lookup is arithmetic
        // plus one array read. Without it the server's idea of where the player is never changes.
        ushort area = world.Terrain
            .GetMap(_player.MapId)
            .GetAreaId(_player.Position.X, _player.Position.Y);

        if (area != 0 && area != _player.ZoneId)
        {
            _player.ZoneId = area;
            Log.ZoneChanged(logger, _player.Name, area);
        }

        // Relayed under the opcode the client used, so other clients animate it the same way —
        // a walk arrives as a walk, a jump as a jump.
        await _map.BroadcastMovementAsync(_player, opcode, _player.Movement, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Corrects a client whose movement was refused.
    /// </summary>
    /// <remarks>
    /// A teleport acknowledgement is how the protocol says "you are actually here". Without it a
    /// rejected client keeps walking in its own reality and every later packet is rejected too.
    /// </remarks>
    private async Task SendKnownPositionAsync(CancellationToken cancellationToken)
    {
        if (_player is null)
        {
            return;
        }

        ServerPacket packet = new(Opcode.MSG_MOVE_TELEPORT_ACK, 64);
        packet.Body.WritePackedGuid(_player.Guid);
        packet.Body.WriteUInt32(0);   // teleport counter

        _player.SyncMovement();
        _player.Movement.WriteTo(packet.Body);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ IPlayerConnection

    /// <summary>Sends another object's create block to this client.</summary>
    /// <remarks>
    /// Players and creatures produce the same block: upstream gives both
    /// <c>UPDATEFLAG_LIVING | UPDATEFLAG_STATIONARY_POSITION</c> in the <c>Unit</c> constructor, and
    /// what differs is the type id and the update type, both of which the builder derives.
    /// </remarks>
    public async Task SendCreateAsync(WorldObject other, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(other);

        other.SyncMovement();

        UpdateData update = new();
        update.AddBlock(UpdateBlockBuilder.BuildCreateBlock(
            other.Guid, other.TypeId, other.Fields, other.Movement, other.Speeds, isSelf: false));

        byte[] payload = update.BuildPayload();
        bool compressed = UpdateData.TryCompress(payload, out byte[] body);

        ServerPacket packet = new(
            compressed ? Opcode.SMSG_COMPRESSED_UPDATE_OBJECT : Opcode.SMSG_UPDATE_OBJECT,
            body.Length);
        packet.Body.WriteBytes(body);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);

        Log.ObjectBecameVisible(logger, other.Name, _player?.Name ?? "?");
    }

    /// <summary>Tells this client to forget an object that has left view.</summary>
    public async Task SendDestroyAsync(ObjectGuid objectGuid, CancellationToken cancellationToken)
    {
        // The out-of-range block is how the client is told to destroy its copy; there is no
        // separate destroy opcode in 3.3.5a.
        UpdateData update = new();
        update.AddOutOfRange(objectGuid);

        byte[] payload = update.BuildPayload();

        ServerPacket packet = new(Opcode.SMSG_UPDATE_OBJECT, payload.Length);
        packet.Body.WriteBytes(payload);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Relays another player's movement to this client.</summary>
    public async Task SendMovementAsync(
        Opcode opcode,
        ObjectGuid mover,
        MovementInfo movement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        ServerPacket packet = new(opcode, 64);
        packet.Body.WritePackedGuid(mover);
        movement.WriteTo(packet.Body);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a character into the world.

    /// <summary>
    /// Accepts a logout and saves the character.
    /// </summary>
    /// <remarks>
    /// Upstream makes the player sit for twenty seconds unless they are resting or a GM. That
    /// timer needs a tick loop to expire on, so logout is instant here — the save is the part that
    /// matters, and delaying it would only risk losing it.
    /// </remarks>
    private async Task HandleLogoutRequestAsync(CancellationToken cancellationToken)
    {
        ServerPacket response = new(Opcode.SMSG_LOGOUT_RESPONSE, 5);
        response.Body.WriteUInt32(0);   // 0 = allowed
        response.Body.WriteUInt8(1);    // instant
        await connection.SendAsync(response, cancellationToken).ConfigureAwait(false);

        await SavePlayerAsync(cancellationToken).ConfigureAwait(false);

        ServerPacket complete = new(Opcode.SMSG_LOGOUT_COMPLETE, 0);
        await connection.SendAsync(complete, cancellationToken).ConfigureAwait(false);

        if (_player is not null)
        {
            if (_map is not null)
            {
                await _map.RemoveAsync(_player, cancellationToken).ConfigureAwait(false);
            }

            Log.PlayerLeftWorld(logger, _player.Name, connection.RemoteAddress);
        }

        _player = null;
        _map = null;

        // Back to character selection: the opcode table stops admitting gameplay opcodes again.
        Status = SessionStatus.Authed;
    }

    private async Task HandleLogoutCancelAsync(CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_LOGOUT_CANCEL_ACK, 0);
        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the player's position back to the database.
    /// </summary>
    /// <remarks>
    /// Called on logout and on disconnect. A client that vanishes mid-session — alt-F4, a dropped
    /// connection — must not lose its progress, and there is no tick loop yet to save periodically.
    /// </remarks>
    public async Task SavePlayerAsync(CancellationToken cancellationToken)
    {
        if (_player is null)
        {
            return;
        }

        await characters.SavePositionAsync(
            _player.Guid.Counter,
            _player.MapId,
            _player.ZoneId,
            _player.Position.X,
            _player.Position.Y,
            _player.Position.Z,
            _player.Position.Orientation,
            cancellationToken).ConfigureAwait(false);

        Log.PlayerSaved(logger, _player.Name, _player.Position.X, _player.Position.Y);

        // A dropped connection never sends a logout, so the map has to be told here too or the
        // player stays visible to everyone else as a statue.
        if (_map is not null)
        {
            await _map.RemoveAsync(_player, cancellationToken).ConfigureAwait(false);
            _map = null;
        }
    }

    /// <summary>
    /// Puts a character into the world.
    /// </summary>
    /// <remarks>
    /// The session leaves <see cref="SessionStatus.Authed"/> here, which is what starts admitting
    /// the gameplay opcodes the table gates on <see cref="SessionStatus.LoggedIn"/>.
    /// </remarks>
    private async Task HandlePlayerLoginAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid))
        {
            return;
        }

        ObjectGuid guid = new(rawGuid);

        IReadOnlyList<CharacterSummary> roster =
            await characters.ListForAccountAsync(_account.Id, cancellationToken).ConfigureAwait(false);

        CharacterSummary? character = roster.FirstOrDefault(entry => entry.Id == guid.Counter);

        // Ownership is checked by looking only at this account's characters, so a forged guid finds
        // nothing rather than someone else's character.
        if (character is null)
        {
            Log.LoginRejected(logger, guid.Counter, "not owned by this account", connection.RemoteAddress);
            return;
        }

        if (!world.TryBuildPlayer(character, out Player? player, out string? reason))
        {
            Log.LoginRejected(logger, character.Id, reason, connection.RemoteAddress);
            return;
        }

        _player = player;
        Status = SessionStatus.LoggedIn;

        await PlayerLogin
            .SendLoginSequenceAsync(connection, player, options.Motd, SendAccountDataTimesAsync, cancellationToken)
            .ConfigureAwait(false);

        await PlayerLogin.SendSelfCreateAsync(connection, player, cancellationToken).ConfigureAwait(false);
        await PlayerLogin.SendTimeSyncRequestAsync(connection, 0, cancellationToken).ConfigureAwait(false);

        // Added after the self create: the client needs to know about itself before it is told
        // about anyone standing next to it.
        player.Connection = this;
        _map = maps.GetMap(player.MapId);
        await _map.AddAsync(player, cancellationToken).ConfigureAwait(false);

        Log.PlayerEnteredWorld(
            logger, player.Name, player.MapId, player.Position.X, player.Position.Y, connection.RemoteAddress);
    }

    private async Task HandleRealmSplitAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        PacketReader reader = new(payload.Span);
        reader.TryReadUInt32(out uint token);

        ServerPacket packet = new(Opcode.SMSG_REALM_SPLIT, 16);
        packet.Body.WriteUInt32(token);
        packet.Body.WriteUInt32(0);              // 0 normal, 1 split, 2 split pending
        packet.Body.WriteCString("01/01/01");

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HandlePingAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt32(out uint ping))
        {
            return false;
        }

        ServerPacket packet = new(Opcode.SMSG_PONG, 4);
        packet.Body.WriteUInt32(ping);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------ parsing

    private readonly record struct AuthSessionRequest(
        uint Build,
        string Account,
        byte[] LocalChallenge,
        uint RealmId,
        byte[] Digest,
        ReadOnlyMemory<byte> AddonInfo);

    private static bool TryParseAuthSession(ReadOnlySpan<byte> payload, out AuthSessionRequest request)
    {
        request = default;

        PacketReader reader = new(payload);

        if (!reader.TryReadUInt32(out uint build))
        {
            return false;
        }

        reader.Skip(4);                                       // login server id

        if (!reader.TryReadCString(out string account))
        {
            return false;
        }

        reader.Skip(4);                                       // login server type

        if (!reader.TryReadBytes(4, out ReadOnlySpan<byte> localChallenge))
        {
            return false;
        }

        byte[] challenge = localChallenge.ToArray();

        reader.Skip(4);                                       // region id
        reader.Skip(4);                                       // battlegroup id

        if (!reader.TryReadUInt32(out uint realmId))
        {
            return false;
        }

        reader.Skip(8);                                       // DoS response

        if (!reader.TryReadBytes(20, out ReadOnlySpan<byte> digest) || !reader.Ok)
        {
            return false;
        }

        byte[] digestBytes = digest.ToArray();

        // Whatever is left is the compressed addon manifest.
        byte[] addonInfo = payload[reader.Position..].ToArray();

        request = new AuthSessionRequest(
            build,
            account.ToUpperInvariant(),
            challenge,
            realmId,
            digestBytes,
            addonInfo);

        return true;
    }

    /// <summary>Formats a build number for logs without pulling in culture handling everywhere.</summary>
    internal static string FormatBuild(uint build) => build.ToString(CultureInfo.InvariantCulture);
}
