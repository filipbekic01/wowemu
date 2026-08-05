using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using WowEmu.Game.Movement;
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
    IInventoryRepository inventory,
    ItemGuidGenerator itemGuids,
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

    /// <summary>
    /// Upstream's <c>MAX_PROCESSED_PACKETS_IN_SAME_WORLDSESSION_UPDATE</c>.
    /// </summary>
    private const int MaxPacketsPerUpdate = 150;

    private readonly byte[] _authSeed = RandomNumberGenerator.GetBytes(4);
    private readonly InboundPackets _inbound = new();

    private readonly List<(ObjectGuid Mover, CreatureMove Move, uint SplineId)> _pendingMonsterMoves = [];
    private readonly List<AttackerState> _pendingMeleeSwings = [];
    private readonly List<SpellDamageLog> _pendingSpellDamage = [];
    private readonly List<PeriodicAuraLog> _pendingAuraLogs = [];

    /// <summary>The last swing failure told to this client, so a run of them is only reported once.</summary>
    private SwingError _lastSwingError;
    private UpdateData _pendingUpdates = new();
    private TickScheduler? _scheduler;
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

    /// <summary>The map this session's character is on, or null at the character screen.</summary>
    public Map? CurrentMap => _map;

    /// <summary>Whether a character is in the world and therefore worth saving.</summary>
    public bool HasPlayerInWorld => _player is not null;

    /// <summary>
    /// Binds this session to the loop that drains its world queue.
    /// </summary>
    /// <remarks>
    /// Called by the world loop when the session joins it. Handlers that await start their work on
    /// this scheduler, so their continuations come back to the tick rather than to the thread pool.
    /// </remarks>
    public void AttachTo(TickScheduler scheduler) => _scheduler = scheduler;

    /// <summary>
    /// Sends the challenge that opens the handshake, then pumps packets until the client goes.
    /// </summary>
    /// <remarks>
    /// Two loops, not one. Reading and writing are independent now that sending is a queue write, so
    /// a client that has stopped reading cannot stall the reader — and the world protocol opens with
    /// the <i>server</i> speaking, which is why the challenge is queued before either loop starts.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        SendAuthChallenge();

        Task sending = connection.RunSendLoopAsync(cancellationToken);

        try
        {
            await connection.RunAsync(HandleAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Drains what is already queued before the loop stops. A rejection sent as the last act
            // of a session still has to reach the client, or it sees a bare disconnect instead of
            // the reason.
            connection.CompleteSending();
            await sending.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Classifies an arriving packet and files it for the loop allowed to run it.
    /// </summary>
    /// <remarks>
    /// This runs on the connection's read task, so it must not handle anything itself: the read task
    /// is not the world tick and is not a map worker, and PLAN.md §4.2 rule 1 forbids it touching
    /// game state. Two opcodes are exceptions, exactly as upstream — the handshake, because no
    /// session exists to queue onto yet, and the ping, because it has to answer even when the
    /// session is busy and touches nothing.
    /// </remarks>
    private async Task<bool> HandleAsync(Opcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        switch (opcode)
        {
            case Opcode.CMSG_AUTH_SESSION:
                return await HandleAuthSessionAsync(payload, cancellationToken).ConfigureAwait(true);

            case Opcode.CMSG_PING:
                return HandlePing(payload);

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

        Enqueue(opcode, payload, info.Value.Processing);
        return true;
    }

    /// <summary>
    /// Files a packet for whichever loop is allowed to run it.
    /// </summary>
    /// <remarks>
    /// The payload is copied because it is not ours: the read loop rents it from a pool and returns
    /// it the moment this returns. Handing the queue a buffer that is about to be reused for another
    /// packet is the kind of bug that shows up as one client receiving another's data.
    /// <para>
    /// Ordering between the two loops is <see cref="InboundPackets"/>'s problem, and worth
    /// reading about there — getting it wrong saved a character in the wrong place.
    /// </para>
    /// </remarks>
    private void Enqueue(Opcode opcode, ReadOnlyMemory<byte> payload, PacketProcessing processing) =>
        _inbound.Enqueue(new InboundPacket(opcode, payload.ToArray(), processing));

    /// <summary>Runs the packets that must run on the world tick.</summary>
    public void DrainWorldPackets(uint diff) => Drain(onMapWorker: false);

    /// <summary>Runs the packets that may run on the owning map's worker.</summary>
    public void DrainMapPackets(uint diff) => Drain(onMapWorker: true);

    /// <summary>
    /// Runs queued packets, up to a budget.
    /// </summary>
    /// <remarks>
    /// The cap is upstream's <c>MAX_PROCESSED_PACKETS_IN_SAME_WORLDSESSION_UPDATE</c>. Without it a
    /// client that floods can hold the tick for as long as it keeps sending, which is a denial of
    /// service against every other player on the server rather than just itself.
    /// </remarks>
    private void Drain(bool onMapWorker)
    {
        int processed = 0;

        while (processed < MaxPacketsPerUpdate
            && _inbound.TryDequeueFor(onMapWorker, _map is not null, out InboundPacket packet))
        {
            processed++;

            try
            {
                Dispatch(packet.Opcode, packet.Payload);
            }
            catch (Exception exception)
            {
                // One malformed packet must not take the tick down. The session survives too:
                // upstream tolerates nonsense from clients, and a disconnect would hide the cause.
                Log.PacketHandlerFailed(logger, exception, packet.Opcode, connection.RemoteAddress);
            }
        }
    }

    /// <summary>
    /// Runs one packet's handler.
    /// </summary>
    /// <remarks>
    /// Handlers that need the database start work on the tick-bound scheduler and return
    /// immediately; their continuations resume at the next drain of the loop that owns them, so the
    /// tick is never blocked on a query and the code still reads top to bottom. That is PLAN.md
    /// §4.2 rule 3, and it is the reason <c>TickScheduler</c> exists.
    /// </remarks>
    private void Dispatch(Opcode opcode, byte[] payload)
    {
        switch (opcode)
        {
            case Opcode.CMSG_READY_FOR_ACCOUNT_DATA_TIMES:
                SendAccountDataTimes(GlobalCacheMask);
                return;

            case Opcode.CMSG_CHAR_ENUM:
                RunOnTick(SendCharacterListAsync);
                return;

            case Opcode.CMSG_CHAR_CREATE:
                RunOnTick(token => HandleCharacterCreateAsync(payload, token));
                return;

            case Opcode.CMSG_CHAR_DELETE:
                RunOnTick(token => HandleCharacterDeleteAsync(payload, token));
                return;

            case Opcode.CMSG_PLAYER_LOGIN:
                RunOnTick(token => HandlePlayerLoginAsync(payload, token));
                return;

            case Opcode.CMSG_REALM_SPLIT:
                HandleRealmSplit(payload);
                return;

            case Opcode.CMSG_LOGOUT_REQUEST:
                RunOnTick(HandleLogoutRequestAsync);
                return;

            case Opcode.CMSG_LOGOUT_CANCEL:
                HandleLogoutCancel();
                return;

            case Opcode.CMSG_ATTACKSWING:
                HandleAttackSwing(payload);
                return;

            case Opcode.CMSG_ATTACKSTOP:
                HandleAttackStop();
                return;

            case Opcode.CMSG_CAST_SPELL:
                HandleCastSpell(payload);
                return;

            case Opcode.CMSG_CANCEL_CAST:
                HandleCancelCast();
                return;

            case Opcode.CMSG_REPOP_REQUEST:
                HandleRepopRequest();
                return;

            case Opcode.CMSG_RECLAIM_CORPSE:
                HandleReclaimCorpse();
                return;

            case Opcode.CMSG_ITEM_QUERY_SINGLE:
                HandleItemQuerySingle(payload);
                return;

            case Opcode.CMSG_SWAP_INV_ITEM:
                HandleSwapInventoryItem(payload);
                return;

            case Opcode.CMSG_SWAP_ITEM:
                HandleSwapItem(payload);
                return;

            case Opcode.CMSG_AUTOEQUIP_ITEM:
                HandleAutoEquipItem(payload);
                return;

            case Opcode.CMSG_AUTOSTORE_BAG_ITEM:
                HandleAutoStoreBagItem(payload);
                return;

            case Opcode.CMSG_SPLIT_ITEM:
                HandleSplitItem(payload);
                return;

            case Opcode.CMSG_DESTROYITEM:
                HandleDestroyItem(payload);
                return;

            case Opcode.CMSG_LOOT:
                HandleLoot(payload);
                return;

            case Opcode.CMSG_AUTOSTORE_LOOT_ITEM:
                HandleAutostoreLootItem(payload);
                return;

            case Opcode.CMSG_LOOT_MONEY:
                HandleLootMoney();
                return;

            case Opcode.CMSG_LOOT_RELEASE:
                HandleLootRelease(payload);
                return;

            case Opcode.CMSG_QUESTGIVER_STATUS_QUERY:
                HandleQuestGiverStatusQuery(payload);
                return;

            case Opcode.CMSG_QUESTGIVER_HELLO:
                HandleQuestGiverHello(payload);
                return;

            case Opcode.CMSG_QUESTGIVER_QUERY_QUEST:
                HandleQuestGiverQueryQuest(payload);
                return;

            case Opcode.CMSG_QUESTGIVER_ACCEPT_QUEST:
                HandleQuestGiverAcceptQuest(payload);
                return;

            case Opcode.CMSG_QUESTGIVER_COMPLETE_QUEST:
                HandleQuestGiverCompleteQuest(payload);
                return;

            case Opcode.CMSG_QUESTGIVER_CHOOSE_REWARD:
                HandleQuestGiverChooseReward(payload);
                return;

            case Opcode.CMSG_QUESTLOG_REMOVE_QUEST:
                HandleQuestLogRemoveQuest(payload);
                return;

            case Opcode.CMSG_GOSSIP_HELLO:
                HandleGossipHello(payload);
                return;

            case Opcode.CMSG_GOSSIP_SELECT_OPTION:
                HandleGossipSelectOption(payload);
                return;

            case Opcode.CMSG_NPC_TEXT_QUERY:
                HandleNpcTextQuery(payload);
                return;

            case Opcode.CMSG_LIST_INVENTORY:
                HandleListInventory(payload);
                return;

            case Opcode.CMSG_BUY_ITEM:
                HandleBuyItem(payload);
                return;

            case Opcode.CMSG_SELL_ITEM:
                HandleSellItem(payload);
                return;

            default:
                // Every movement opcode routes to one handler, exactly as upstream does — the
                // opcode says what the client thinks it is doing, but the payload is identical.
                if (OpcodeTable.TryGet(opcode, out OpcodeInfo? info)
                    && info.Value.UpstreamHandler == "HandleMovementOpcodes")
                {
                    HandleMovement(opcode, payload);
                    return;
                }

                Log.UnhandledOpcode(logger, opcode, connection.RemoteAddress);
                return;
        }
    }

    /// <summary>
    /// Starts asynchronous work that will resume on the loop that started it.
    /// </summary>
    /// <remarks>
    /// The scheduler is whichever loop is draining this session right now. Its continuations run at
    /// that loop's next drain point, which is what keeps a database answer from landing on a thread
    /// pool thread and touching a player from outside its map's worker.
    /// </remarks>
    private void RunOnTick(Func<CancellationToken, Task> work)
    {
        // Every await inside a handler is ConfigureAwait(true), so the TickSynchronizationContext
        // this scheduler installed brings the continuation back to the loop. Without that, a
        // handler would resume on a thread-pool thread and carry on touching player and map state
        // from outside the loop that owns it — PLAN.md §4.2 rule 1, violated silently.

        TickScheduler scheduler = _scheduler
            ?? throw new InvalidOperationException("A session was drained before it was attached to a loop.");

        _ = scheduler.Factory.StartNew(
            async () =>
            {
                try
                {
                    await work(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    Log.DeferredWorkFailed(logger, exception, connection.RemoteAddress);
                }
            },
            CancellationToken.None).Unwrap();
    }

    // ------------------------------------------------------------------ handshake

    private void SendAuthChallenge()
    {
        ServerPacket packet = new(Opcode.SMSG_AUTH_CHALLENGE, 40);

        packet.Body.WriteUInt32(1);
        packet.Body.WriteBytes(_authSeed);

        // 32 bytes upstream labels "new encryption seeds". The 3.3.5a client reads and discards
        // them; they matter to later expansions. Sent because the client expects 40 bytes.
        packet.Body.WriteBytes(RandomNumberGenerator.GetBytes(32));

        connection.Send(packet);
    }

    private async Task<bool> HandleAuthSessionAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (!TryParseAuthSession(payload.Span, out AuthSessionRequest request))
        {
            Log.MalformedAuthSession(logger, connection.RemoteAddress);
            return false;
        }

        _account = await accounts.FindAsync(request.Account, cancellationToken).ConfigureAwait(true);

        if (_account?.SessionKey is null)
        {
            // No key means the account never completed a logon, so there is nothing to encrypt
            // with — the client cannot read this response, but upstream sends it anyway.
            Log.UnknownAccount(logger, request.Account, connection.RemoteAddress);
            await SendAuthResponseErrorAsync(AuthUnknownAccount, cancellationToken).ConfigureAwait(true);
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
            await SendAuthResponseErrorAsync(AuthFailed, cancellationToken).ConfigureAwait(true);
            return false;
        }

        if (!VerifyDigest(request, _account.SessionKey))
        {
            Log.BadDigest(logger, _account.Username, connection.RemoteAddress);
            await SendAuthResponseErrorAsync(AuthFailed, cancellationToken).ConfigureAwait(true);
            return false;
        }

        _authenticated = true;
        Log.Authenticated(logger, _account.Username, request.Build, connection.RemoteAddress);

        await SendAuthResponseAsync(cancellationToken).ConfigureAwait(true);
        await SendAddonInfoAsync(request.AddonInfo, cancellationToken).ConfigureAwait(true);
        await SendClientCacheVersionAsync(cancellationToken).ConfigureAwait(true);
        await SendTutorialFlagsAsync(cancellationToken).ConfigureAwait(true);

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

        connection.Send(packet);
    }

    private async Task SendAuthResponseErrorAsync(byte code, CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_AUTH_RESPONSE, 1);
        packet.Body.WriteUInt8(code);

        connection.Send(packet);
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

        connection.Send(packet);
    }

    private async Task SendClientCacheVersionAsync(CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_CLIENTCACHE_VERSION, 4);
        packet.Body.WriteUInt32(options.ClientCacheVersion);

        connection.Send(packet);
    }

    private async Task SendTutorialFlagsAsync(CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_TUTORIAL_FLAGS, TutorialValueCount * 4);

        for (int i = 0; i < TutorialValueCount; i++)
        {
            packet.Body.WriteUInt32(0);
        }

        connection.Send(packet);
    }

    private void SendAccountDataTimes(uint mask)
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

        connection.Send(packet);
    }

    /// <summary>
    /// Answers <c>CMSG_CHAR_ENUM</c> from the characters database.
    /// </summary>
    /// <remarks>
    /// Built from real rows in the characters database, equipment included — the selection screen
    /// draws each character wearing what it owns, and a naked one there looks like data loss even
    /// when the items are safely in the database.
    /// </remarks>
    private async Task SendCharacterListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CharacterSummary> roster = _account is null
            ? []
            : await characters.ListForAccountAsync(_account.Id, cancellationToken).ConfigureAwait(true);

        Dictionary<uint, CharacterList.VisibleItem[]> worn = [];

        foreach (CharacterSummary character in roster)
        {
            worn[character.Id] = await VisibleEquipmentAsync(character.Id, cancellationToken)
                .ConfigureAwait(true);
        }

        ServerPacket packet = new(Opcode.SMSG_CHAR_ENUM, 1 + (roster.Count * CharacterList.MaxBytesPerCharacter));
        CharacterList.Write(packet.Body, roster, worn);

        connection.Send(packet);

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
            await SendCharacterCreateResultAsync(CharCreateError, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (!CharacterName.TryNormalize(rawName, out string name))
        {
            await SendCharacterCreateResultAsync(CharNameInvalidCharacter, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (gender > 1 || !createInfo.TryGet(race, characterClass, out PlayerCreateInfo start))
        {
            Log.InvalidCharacterCreate(logger, race, characterClass, gender, connection.RemoteAddress);
            await SendCharacterCreateResultAsync(CharCreateFailed, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (await characters.CountForAccountAsync(_account.Id, cancellationToken).ConfigureAwait(true)
            >= MaxCharactersPerAccount)
        {
            await SendCharacterCreateResultAsync(CharCreateAccountLimit, cancellationToken).ConfigureAwait(true);
            return;
        }

        if (await characters.NameExistsAsync(name, cancellationToken).ConfigureAwait(true))
        {
            await SendCharacterCreateResultAsync(CharCreateNameInUse, cancellationToken).ConfigureAwait(true);
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

        uint? id = await characters.CreateAsync(character, cancellationToken).ConfigureAwait(true);

        if (id is null)
        {
            // Lost the race on the unique index — someone else took the name in between.
            await SendCharacterCreateResultAsync(CharCreateNameInUse, cancellationToken).ConfigureAwait(true);
            return;
        }

        character.Id = id.Value;
        await GiveStartingGearAsync(character, cancellationToken).ConfigureAwait(true);

        Log.CharacterCreated(logger, name, id.Value, _account.Username, connection.RemoteAddress);
        await SendCharacterCreateResultAsync(CharCreateSuccess, cancellationToken).ConfigureAwait(true);
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
            await SendCharacterDeleteResultAsync(CharDeleteFailed, cancellationToken).ConfigureAwait(true);
            return;
        }

        ObjectGuid guid = new(rawGuid);

        // Ownership is verified in the delete itself, so a client asking to delete someone else's
        // character simply finds nothing to delete.
        bool deleted = await characters
            .DeleteAsync(_account.Id, guid.Counter, cancellationToken)
            .ConfigureAwait(true);

        if (deleted)
        {
            // After the character, not before: a failed delete must not strip an inventory the
            // character still has.
            await inventory.DeleteForCharacterAsync(guid.Counter, cancellationToken).ConfigureAwait(true);

            Log.CharacterDeleted(logger, guid.Counter, _account.Username, connection.RemoteAddress);
        }

        await SendCharacterDeleteResultAsync(deleted ? CharDeleteSuccess : CharDeleteFailed, cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task SendCharacterCreateResultAsync(byte result, CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_CHAR_CREATE, 1);
        packet.Body.WriteUInt8(result);

        connection.Send(packet);
    }

    private async Task SendCharacterDeleteResultAsync(byte result, CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_CHAR_DELETE, 1);
        packet.Body.WriteUInt8(result);

        connection.Send(packet);
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
    /// <summary>
    /// Starts auto-attacking whatever the client clicked.
    /// </summary>
    /// <remarks>
    /// Port of <c>WorldSession::HandleAttackSwingOpcode</c>.
    /// <para>
    /// Every rejection answers with a stop rather than with silence. The client has already started
    /// its own attack animation by the time this arrives — that is why it sent the packet — so
    /// ignoring an invalid target leaves it swinging at nothing indefinitely.
    /// </para>
    /// </remarks>
    private void HandleAttackSwing(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong raw))
        {
            return;
        }

        // A full guid, not a packed one: this is one of the handful of opcodes that sends them whole.
        ObjectGuid targetGuid = new(raw);

        if (_map.Find(targetGuid) is not Unit target || !target.IsAlive || ReferenceEquals(target, _player))
        {
            SendAttackState(_player.Guid, targetGuid.IsEmpty ? null : targetGuid, attacking: false, victimIsDead: false);
            return;
        }

        if (!_player.Attack(target))
        {
            // Nothing changed — already attacking this one. Re-sending the start would restart the
            // animation from the beginning, which reads as a stutter every time the client re-asks.
            return;
        }

        SendAttackState(_player.Guid, target.Guid, attacking: true, victimIsDead: false);

        Log.AttackStarted(logger, _player.Name, target.Name, connection.RemoteAddress);
    }

    /// <summary>Stops auto-attacking.</summary>
    private void HandleAttackStop()
    {
        if (_player is null)
        {
            return;
        }

        ObjectGuid? victim = _player.Victim?.Guid;
        bool victimIsDead = _player.Victim is { IsAlive: false };

        _player.AttackStop();

        SendAttackState(_player.Guid, victim, attacking: false, victimIsDead: victimIsDead);
    }

    /// <summary>
    /// Starts a cast.
    /// </summary>
    /// <remarks>
    /// Port of <c>WorldSession::HandleCastSpellOpcode</c> plus the parts of <c>Spell::prepare</c>
    /// that decide whether the attempt is allowed.
    /// <para>
    /// An instant spell is finished here rather than on the next tick. Waiting would put a tick of
    /// latency on every instant ability, and the client has already played its animation.
    /// </para>
    /// </remarks>
    private void HandleCastSpell(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!SpellCast.TryRead(ref reader, out byte castCount, out uint spellId, out SpellCastTargets targets))
        {
            return;
        }

        if (!world.Spells.Spells.TryGet(spellId, out SpellEntry spell))
        {
            // An id the client data does not describe. Nothing sensible to say about it, so the
            // client is told its attempt failed rather than left waiting.
            SendCastFailed(castCount, spellId, SpellCastResult.NotKnown);
            return;
        }

        Unit? target = targets.HasObjectTarget ? _map.Find(targets.ObjectTarget) as Unit : null;

        SpellCastResult result = SpellCastChecks.Check(
            _player,
            spell,
            target,
            world.Spells,
            _player.Casting,
            () => target is null || _map.IsInLineOfSight(_player.Position, target.Position));

        if (result != SpellCastResult.Ok)
        {
            SendCastFailed(castCount, spellId, result);
            return;
        }

        int castTimeMs = world.Spells.CastTimeMs(spell);

        // Power is taken when the cast *starts*, not when it lands. Taking it on completion would
        // make a cancelled cast free, which is a way to hold a spell ready at no cost.
        uint cost = SpellCastChecks.PowerCost(_player, spell);

        if (cost > 0 && spell.PowerType == _player.PowerType)
        {
            _player.Power -= Math.Min(cost, _player.Power);
        }

        _player.Casting.StartGlobalCooldown(spell);

        SendSpellStart(_player.Guid, spellId, castCount, castTimeMs, target?.Guid ?? ObjectGuid.Empty, _player.Power);

        if (castTimeMs <= 0)
        {
            // Instant: completed here rather than on the next tick. Waiting would put a tick of
            // latency on every instant ability, and the client has already played its animation.
            _map.CompleteCast(_player, spell, target, castCount);
        }
        else
        {
            _player.Casting.Begin(spell, target, castCount, castTimeMs);
        }

        // The name is built into a local: the analyzer objects to work inside a log call, and
        // ToString() concatenates a rank onto every cast whether or not debug logging is on.
        string spellName = spell.Name;

        Log.SpellCast(logger, _player.Name, spellName, target?.Name ?? "self", connection.RemoteAddress);
    }

    /// <summary>Abandons the cast in progress.</summary>
    /// <remarks>
    /// The power spent on it is not refunded, which is upstream's behaviour and the reason
    /// cancelling a cast is a real cost rather than free.
    /// </remarks>
    private void HandleCancelCast() => _player?.Casting.Cancel();

    /// <summary>
    /// Releases the spirit — the client's "Release" button.
    /// </summary>
    /// <remarks>
    /// Port of <c>WorldSession::HandleRepopRequestOpcode</c>. A living player asking to release is
    /// ignored rather than refused: the client sends it whenever the button is visible, and the
    /// button outlives the state it belongs to by a tick or two.
    /// </remarks>
    private void HandleRepopRequest()
    {
        if (_player is null || _map is null || _player.IsAlive || _player.IsGhost)
        {
            return;
        }

        if (_map.ReleaseSpirit(_player))
        {
            Log.PlayerReleased(logger, _player.Name, connection.RemoteAddress);
        }
    }

    /// <summary>
    /// Resurrects at the corpse — the client's "Resurrect" button.
    /// </summary>
    /// <remarks>
    /// Silently ignored when out of range. The client only shows the button when it thinks the
    /// corpse is close, so a refusal here means the two disagree about where things are, and a
    /// message about it would be noise rather than information.
    /// </remarks>
    private void HandleReclaimCorpse()
    {
        if (_player is null || _map is null)
        {
            return;
        }

        if (_map.ReclaimCorpse(_player))
        {
            Log.PlayerResurrected(logger, _player.Name, connection.RemoteAddress);
        }
    }

    /// <summary>
    /// Answers <c>CMSG_ITEM_QUERY_SINGLE</c> — what is this item?
    /// </summary>
    /// <remarks>
    /// The client asks about anything it has a guid for but no cached tooltip, which after a cache
    /// wipe is everything it owns. It blocks the tooltip on the answer, so an unanswered query is a
    /// slot that never shows what it holds.
    /// <para>
    /// A missing entry is answered too, with the high bit set — see
    /// <see cref="ItemQueryResponse.NotFoundFlag"/>. Silence would leave the client asking again.
    /// </para>
    /// </remarks>
    private void HandleItemQuerySingle(ReadOnlyMemory<byte> payload)
    {
        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt32(out uint entry))
        {
            return;
        }

        ServerPacket packet = new(Opcode.SMSG_ITEM_QUERY_SINGLE_RESPONSE, 600);

        if (world.Items.TryGet(entry, out ItemTemplate? template) && template is not null)
        {
            ItemQueryResponse.Write(packet.Body, template, TryGetSpellCooldown);
        }
        else
        {
            ItemQueryResponse.WriteNotFound(packet.Body, entry);
        }

        connection.Send(packet);
    }

    /// <summary>
    /// Moves something between two slots on the player itself. <c>CMSG_SWAP_INV_ITEM</c>.
    /// </summary>
    /// <remarks>
    /// <b>The destination is read first.</b> Three of the six inventory opcodes put the destination
    /// ahead of the source, and reading them the intuitive way round swaps the drag — the item the
    /// player picked up stays put and the one under the cursor moves.
    /// </remarks>
    private void HandleSwapInventoryItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt8(out byte destinationSlot) || !reader.TryReadUInt8(out byte sourceSlot))
        {
            return;
        }

        Move(
            new ItemPosition(InventorySlots.Backpack, sourceSlot),
            new ItemPosition(InventorySlots.Backpack, destinationSlot));
    }

    /// <summary>Moves something between any two positions, bags included. <c>CMSG_SWAP_ITEM</c>.</summary>
    /// <inheritdoc cref="HandleSwapInventoryItem" path="/remarks"/>
    private void HandleSwapItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt8(out byte destinationBag) || !reader.TryReadUInt8(out byte destinationSlot)
            || !reader.TryReadUInt8(out byte sourceBag) || !reader.TryReadUInt8(out byte sourceSlot))
        {
            return;
        }

        Move(new ItemPosition(sourceBag, sourceSlot), new ItemPosition(destinationBag, destinationSlot));
    }

    /// <summary>
    /// Wears whatever the player double-clicked. <c>CMSG_AUTOEQUIP_ITEM</c>.
    /// </summary>
    /// <remarks>
    /// The slot is chosen here rather than by the client, which sends only where the item came
    /// from. Swapping is allowed for anything but a bag: a second ring replaces one that is already
    /// worn, but a bag with things in it must be emptied first.
    /// </remarks>
    private void HandleAutoEquipItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt8(out byte sourceBag) || !reader.TryReadUInt8(out byte sourceSlot))
        {
            return;
        }

        ItemPosition from = new(sourceBag, sourceSlot);

        if (_player.Inventory.Get(from) is not { } item)
        {
            SendEquipError(InventoryResult.ItemNotFound);
            return;
        }

        byte slot = _player.Inventory.FindEquipSlot(item.Template, swap: item is not Bag);

        if (slot == InventorySlots.None)
        {
            SendEquipError(InventoryResult.ItemCantBeEquipped, item.Guid);
            return;
        }

        InventoryResult result = _player.Inventory.Equip(from, slot);

        if (result != InventoryResult.Ok)
        {
            SendEquipError(result, item.Guid, item.Template.RequiredLevel);
        }
    }

    /// <summary>Takes something off and puts it in the first free slot. <c>CMSG_AUTOSTORE_BAG_ITEM</c>.</summary>
    private void HandleAutoStoreBagItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt8(out byte sourceBag) || !reader.TryReadUInt8(out byte sourceSlot)
            || !reader.TryReadUInt8(out byte destinationBag))
        {
            return;
        }

        ItemPosition from = new(sourceBag, sourceSlot);

        if (_player.Inventory.Get(from) is not { } item)
        {
            SendEquipError(InventoryResult.ItemNotFound);
            return;
        }

        if (FirstFreeSlotIn(destinationBag) is not { } to)
        {
            SendEquipError(InventoryResult.BagFull, item.Guid);
            return;
        }

        Move(from, to);
    }

    /// <summary>Splits a stack in two. <c>CMSG_SPLIT_ITEM</c>.</summary>
    private void HandleSplitItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt8(out byte sourceBag) || !reader.TryReadUInt8(out byte sourceSlot)
            || !reader.TryReadUInt8(out byte destinationBag) || !reader.TryReadUInt8(out byte destinationSlot)
            || !reader.TryReadUInt32(out uint count))
        {
            return;
        }

        ItemPosition from = new(sourceBag, sourceSlot);
        ItemPosition to = new(destinationBag, destinationSlot);

        if (from == to || count == 0)
        {
            return;
        }

        InventoryResult result = _player.Inventory.Split(from, to, count, itemGuids.Next);

        if (result != InventoryResult.Ok)
        {
            SendEquipError(result, _player.Inventory.Get(from)?.Guid ?? default);
        }
    }

    /// <summary>Throws something away. <c>CMSG_DESTROYITEM</c>.</summary>
    /// <remarks>
    /// The item is gone for good — there is no buyback and nothing drops on the ground. The client
    /// has already asked the player to confirm.
    /// </remarks>
    private void HandleDestroyItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt8(out byte bag) || !reader.TryReadUInt8(out byte slot)
            || !reader.TryReadUInt8(out byte count))
        {
            return;
        }

        ItemPosition position = new(bag, slot);
        ObjectGuid destroyed = _player.Inventory.Get(position)?.Guid ?? default;

        InventoryResult result = _player.Inventory.Destroy(position, count, out Item? removed);

        if (result != InventoryResult.Ok)
        {
            SendEquipError(result, destroyed);
            return;
        }

        if (removed is not null)
        {
            // The client is told to forget the object as well as the slot: the slot guid going to
            // zero empties the square, but the item object would linger in its cache.
            _knownItems.Remove(removed.Guid);
            _pendingUpdates.AddOutOfRange(removed.Guid);
        }
    }

    /// <summary>Opens a corpse. <c>CMSG_LOOT</c>.</summary>
    /// <remarks>
    /// A dead player cannot loot, which the client also enforces — but the client is not the
    /// authority, and a corpse run past a fresh kill would otherwise empty it.
    /// </remarks>
    private void HandleLoot(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null || !_player.IsAlive)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid))
        {
            return;
        }

        _map.OpenLoot(_player, new ObjectGuid(rawGuid));
    }

    /// <summary>Takes one slot out of the open window. <c>CMSG_AUTOSTORE_LOOT_ITEM</c>.</summary>
    /// <remarks>
    /// One byte, and no guid: the client does not say what it is looting from, so the server has to
    /// remember which window it opened.
    /// </remarks>
    private void HandleAutostoreLootItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt8(out byte slot))
        {
            return;
        }

        _map.TakeLoot(_player, slot);
    }

    /// <summary>Takes the money. <c>CMSG_LOOT_MONEY</c>, which carries no body at all.</summary>
    private void HandleLootMoney()
    {
        if (_player is not null && _map is not null)
        {
            _map.TakeLootMoney(_player);
        }
    }

    /// <summary>Closes the window. <c>CMSG_LOOT_RELEASE</c>.</summary>
    private void HandleLootRelease(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        // The guid is read and not used: the client names what it is closing, and the server
        // already knows. Reading it keeps the parse honest about the packet's shape.
        PacketReader reader = new(payload.Span);
        reader.TryReadUInt64(out _);

        _map.ReleaseLoot(_player);
    }

    /// <summary>Tells this client what a corpse is holding.</summary>
    public void SendLootWindow(ObjectGuid target, byte lootType, uint gold, IReadOnlyList<LootSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        ServerPacket packet = new(Opcode.SMSG_LOOT_RESPONSE, 32 + (slots.Count * 24));
        LootResponse.Write(packet.Body, target, lootType, gold, slots);

        connection.Send(packet);
    }

    /// <summary>Tells this client the window could not be opened.</summary>
    public void SendLootError(ObjectGuid target, LootError reason)
    {
        ServerPacket packet = new(Opcode.SMSG_LOOT_RESPONSE, 10);
        LootResponse.WriteError(packet.Body, target, (byte)reason);

        connection.Send(packet);
    }

    /// <summary>Tells this client one slot has been taken.</summary>
    public void SendLootRemoved(byte slot)
    {
        ServerPacket packet = new(Opcode.SMSG_LOOT_REMOVED, 1);
        LootResponse.WriteRemoved(packet.Body, slot);

        connection.Send(packet);
    }

    /// <summary>
    /// Tells this client the money is gone from the window, and how much it got.
    /// </summary>
    /// <remarks>
    /// Two packets. <c>SMSG_LOOT_CLEAR_MONEY</c> empties the window's coin line and carries no
    /// body; the notify is the chat message. Sending only the notify leaves the coins drawn.
    /// </remarks>
    public void SendLootMoneyTaken(uint copper)
    {
        ServerPacket cleared = new(Opcode.SMSG_LOOT_CLEAR_MONEY, 0);
        connection.Send(cleared);

        ServerPacket notify = new(Opcode.SMSG_LOOT_MONEY_NOTIFY, 5);
        LootResponse.WriteMoneyNotify(notify.Body, copper);

        connection.Send(notify);
    }

    /// <summary>Tells this client the window is closed.</summary>
    public void SendLootReleased(ObjectGuid target)
    {
        ServerPacket packet = new(Opcode.SMSG_LOOT_RELEASE_RESPONSE, 9);
        LootResponse.WriteRelease(packet.Body, target);

        connection.Send(packet);
    }

    /// <summary>Tells this client an item has arrived in its bags.</summary>
    public void SendItemPushed(in ItemPushResult push)
    {
        ServerPacket packet = new(Opcode.SMSG_ITEM_PUSH_RESULT, 48);
        ItemPushResultPacket.Write(packet.Body, push);

        connection.Send(packet);
    }

    /// <summary>The questgiver flag. <c>UNIT_NPC_FLAG_QUESTGIVER</c>.</summary>
    private const uint NpcFlagQuestGiver = 0x0002;

    /// <summary>
    /// Opens an NPC's gossip window. <c>CMSG_GOSSIP_HELLO</c>.
    /// </summary>
    /// <remarks>
    /// The quests ride in the same packet as the gossip lines, which is what puts both in one
    /// window. Sending them separately produces two, and the client closes one of them at once.
    /// <para>
    /// An NPC with no gossip menu of its own and something to offer goes straight to that thing —
    /// a pure vendor opens its stock, a pure questgiver its quest. Showing an empty gossip window
    /// first is what upstream avoids by the same route.
    /// </para>
    /// </remarks>
    private void HandleGossipHello(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || FindInteractable(new ObjectGuid(rawGuid)) is not { } npc)
        {
            return;
        }

        SendGossipFor(npc);
    }

    /// <summary>Clicks one gossip line. <c>CMSG_GOSSIP_SELECT_OPTION</c>.</summary>
    /// <remarks>
    /// The option's own id is echoed back, not its position in the list. A menu with a gap in its
    /// ids would otherwise select the wrong line.
    /// </remarks>
    private void HandleGossipSelectOption(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || !reader.TryReadUInt32(out uint menuId)
            || !reader.TryReadUInt32(out uint optionId))
        {
            return;
        }

        if (FindInteractable(new ObjectGuid(rawGuid)) is not { } npc)
        {
            return;
        }

        GossipMenuOption? chosen = null;

        foreach (GossipMenuOption option in world.Gossip.OptionsFor(menuId))
        {
            if (option.OptionId == optionId && Offers(npc, option))
            {
                chosen = option;
                break;
            }
        }

        if (chosen is null)
        {
            CloseGossip();
            return;
        }

        switch (chosen.OptionType)
        {
            case GossipOption.Vendor:
                SendVendorList(npc);
                break;

            case GossipOption.Gossip when chosen.ActionMenuId != 0:
                SendGossipMenu(npc, chosen.ActionMenuId);
                break;

            default:
                // Trainers, flight masters, bankers, innkeepers and the rest. The line is drawn
                // because the NPC really has the flag; clicking it closes the window rather than
                // opening something that does not exist.
                CloseGossip();
                break;
        }
    }

    /// <summary>Answers <c>CMSG_NPC_TEXT_QUERY</c> — what does this text id say?</summary>
    /// <remarks>
    /// The client asks for anything it has no cached text for, and blocks the gossip window on the
    /// answer. Silence leaves an empty frame on screen.
    /// </remarks>
    private void HandleNpcTextQuery(ReadOnlyMemory<byte> payload)
    {
        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt32(out uint textId))
        {
            return;
        }

        ServerPacket packet = new(Opcode.SMSG_NPC_TEXT_UPDATE, 512);
        GossipPackets.WriteNpcText(packet.Body, textId, world.Gossip.TextFor(textId));

        connection.Send(packet);
    }

    /// <summary>Opens a vendor's stock. <c>CMSG_LIST_INVENTORY</c>.</summary>
    private void HandleListInventory(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null || !_player.IsAlive)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || FindInteractable(new ObjectGuid(rawGuid)) is not { } npc)
        {
            return;
        }

        SendVendorList(npc);
    }

    /// <summary>
    /// Buys something. <c>CMSG_BUY_ITEM</c>.
    /// </summary>
    /// <remarks>
    /// <b>The slot arrives one-based and is decremented here</b>, matching what the list sent out.
    /// A slot of zero is not a real slot; upstream treats it as a forged packet and so does this.
    /// </remarks>
    private void HandleBuyItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || !reader.TryReadUInt32(out uint itemId)
            || !reader.TryReadUInt32(out uint slot) || !reader.TryReadUInt8(out byte count))
        {
            return;
        }

        ObjectGuid vendorGuid = new(rawGuid);

        if (slot == 0 || FindInteractable(vendorGuid) is not { } vendor)
        {
            return;
        }

        Buy(vendor, vendorGuid, itemId, slot, Math.Max(count, (byte)1));
    }

    /// <summary>Sells something. <c>CMSG_SELL_ITEM</c>.</summary>
    /// <remarks>
    /// A successful sale answers with nothing at all: the item leaving the bag and the money
    /// arriving are both field updates, and the client works out the rest.
    /// </remarks>
    private void HandleSellItem(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || !reader.TryReadUInt64(out ulong rawItem)
            || !reader.TryReadUInt8(out byte count))
        {
            return;
        }

        ObjectGuid vendorGuid = new(rawGuid);
        ObjectGuid itemGuid = new(rawItem);

        if (FindInteractable(vendorGuid) is null)
        {
            SendSellFailed(vendorGuid, itemGuid, SellResult.CantFindVendor);
            return;
        }

        if (FindOwnedItem(itemGuid) is not (ItemPosition position, Item item))
        {
            SendSellFailed(vendorGuid, itemGuid, SellResult.CantFindItem);
            return;
        }

        if (item is Bag bag && !bag.IsEmpty)
        {
            SendSellFailed(vendorGuid, itemGuid, SellResult.OnlyEmptyBag);
            return;
        }

        if (item.Template.SellPrice == 0)
        {
            SendSellFailed(vendorGuid, itemGuid, SellResult.CantSellItem);
            return;
        }

        // Zero means the whole stack, which is what "sell all" sends.
        uint selling = count == 0 ? item.Count : count;

        if (selling > item.Count)
        {
            SendSellFailed(vendorGuid, itemGuid, SellResult.CantSellItem);
            return;
        }

        _player.Inventory.Destroy(position, selling, out Item? removed);
        _player.Money += item.Template.SellPrice * selling;

        if (removed is not null)
        {
            _knownItems.Remove(removed.Guid);
            _pendingUpdates.AddOutOfRange(removed.Guid);
        }
    }

    /// <summary>Runs one purchase, reporting whatever went wrong.</summary>
    private void Buy(Creature vendor, ObjectGuid vendorGuid, uint itemId, uint oneBasedSlot, uint count)
    {
        if (_player is null)
        {
            return;
        }

        IReadOnlyList<VendorItem> stock = world.Vendors.For(vendor.Entry);
        int index = (int)oneBasedSlot - 1;

        if (index < 0 || index >= stock.Count || stock[index].ItemId != itemId)
        {
            SendBuyFailed(vendorGuid, itemId, BuyResult.CantFindItem);
            return;
        }

        VendorItem line = stock[index];

        // Honour, arena points and tokens. The cost is a row in a DBC nothing reads, so the only
        // honest answer is to refuse rather than hand the item over for nothing.
        if (!line.IsGoldPurchase)
        {
            SendBuyFailed(vendorGuid, itemId, BuyResult.CantFindItem);
            return;
        }

        if (!world.Items.TryGet(itemId, out ItemTemplate? template) || template is null)
        {
            SendBuyFailed(vendorGuid, itemId, BuyResult.CantFindItem);
            return;
        }

        // The count the client sends is a number of *stacks* of BuyCount, which is why a stack of
        // twenty arrows costs the same as one arrow times twenty.
        uint quantity = count * Math.Max(template.BuyCount, (byte)1);
        uint price = (uint)Math.Max(template.BuyPrice, 0) * count;

        if (_player.Money < price)
        {
            SendBuyFailed(vendorGuid, itemId, BuyResult.NotEnoughMoney);
            return;
        }

        if (_player.Inventory.CanStore(template, quantity, out _) != InventoryResult.Ok)
        {
            SendEquipError(InventoryResult.InventoryFull);
            return;
        }

        _player.Inventory.Store(template, quantity, itemGuids.Next, out IReadOnlyList<Item> bought);
        _player.Money -= price;

        ServerPacket packet = new(Opcode.SMSG_BUY_ITEM, 24);

        // Unlimited stock is -1, not zero: zero would grey the line out as sold.
        GossipPackets.WriteBought(packet.Body, vendorGuid, oneBasedSlot, inStock: -1, count);

        connection.Send(packet);

        foreach (Item item in bought)
        {
            ItemPosition? where = _player.Inventory.PositionOf(item);

            SendItemPushed(new ItemPushResult(
                Player: _player.Guid,
                FromNpc: true,
                Created: false,
                ShowInChat: true,
                Bag: where?.Bag ?? InventorySlots.Backpack,
                Slot: where?.Slot ?? 0,
                Entry: itemId,
                Count: quantity,
                TotalOfEntry: _player.Inventory.CountOf(itemId)));
        }
    }

    /// <summary>Sends whichever window an NPC should open.</summary>
    private void SendGossipFor(Creature npc)
    {
        if (_player is null)
        {
            return;
        }

        // An NPC with no menu of its own goes straight to whatever it does. Showing an empty
        // gossip window first is a click the player should not have to make.
        if (npc.GossipMenuId == 0)
        {
            if ((npc.NpcFlags & NpcFlags.Vendor) != 0)
            {
                SendVendorList(npc);
                return;
            }

            if ((npc.NpcFlags & NpcFlagQuestGiver) != 0)
            {
                HandleQuestGiverHello(BitConverter.GetBytes(npc.Guid.Value));
                return;
            }
        }

        SendGossipMenu(npc, npc.GossipMenuId);
    }

    private void SendGossipMenu(Creature npc, uint menuId)
    {
        if (_player is null)
        {
            return;
        }

        List<GossipLine> lines = [];

        foreach (GossipMenuOption option in world.Gossip.OptionsFor(menuId))
        {
            if (!Offers(npc, option))
            {
                continue;
            }

            lines.Add(new GossipLine(
                Index: option.OptionId,
                Icon: option.Icon,
                Coded: option.BoxCoded,
                BoxMoney: option.BoxMoney,
                Text: option.Text,
                BoxText: option.BoxText));
        }

        List<QuestMenuEntry> quests = [];

        foreach (uint questId in QuestsFor(npc))
        {
            if (!world.Quests.TryGet(questId, out QuestTemplate? quest) || quest is null)
            {
                continue;
            }

            uint icon = MenuIconFor(quest);

            if (icon != QuestGiverStatus.None)
            {
                quests.Add(new QuestMenuEntry(quest.Id, icon, quest.Level, quest.Flags, quest.LogTitle));
            }
        }

        ServerPacket packet = new(
            Opcode.SMSG_GOSSIP_MESSAGE, 32 + (lines.Count * 128) + (quests.Count * 96));

        GossipPackets.WriteGossipMenu(
            packet.Body, npc.Guid, menuId, world.Gossip.TextIdFor(menuId), lines, quests);

        connection.Send(packet);
    }

    private void SendVendorList(Creature vendor)
    {
        if (_player is null)
        {
            return;
        }

        List<VendorLine> lines = [];
        IReadOnlyList<VendorItem> stock = world.Vendors.For(vendor.Entry);

        for (int i = 0; i < stock.Count && lines.Count < VendorStore.MaxItems; i++)
        {
            if (!world.Items.TryGet(stock[i].ItemId, out ItemTemplate? template) || template is null)
            {
                continue;
            }

            lines.Add(new VendorLine(
                // One-based: the client subtracts one before sending a purchase back, so a
                // zero-based slot here buys the item before the one that was clicked.
                Slot: (uint)(i + 1),
                ItemId: template.Entry,
                DisplayId: template.DisplayId,

                // Unlimited stock, which is what a maxcount of zero means and what nearly every
                // row has. A real count would need the restock timer this phase does not run.
                InStock: -1,
                Price: (uint)Math.Max(template.BuyPrice, 0),
                MaxDurability: template.MaxDurability,
                BuyCount: Math.Max(template.BuyCount, (byte)1),
                ExtendedCost: stock[i].ExtendedCost));
        }

        ServerPacket packet = new(Opcode.SMSG_LIST_INVENTORY, 16 + (lines.Count * 32));
        GossipPackets.WriteVendorList(packet.Body, vendor.Guid, lines);

        connection.Send(packet);
    }

    /// <summary>Tells the client to shut the gossip window.</summary>
    private void CloseGossip()
    {
        ServerPacket packet = new(Opcode.SMSG_GOSSIP_COMPLETE, 0);
        connection.Send(packet);
    }

    private void SendBuyFailed(ObjectGuid vendor, uint itemId, BuyResult reason)
    {
        ServerPacket packet = new(Opcode.SMSG_BUY_FAILED, 16);
        GossipPackets.WriteBuyFailed(packet.Body, vendor, itemId, reason);

        connection.Send(packet);
    }

    private void SendSellFailed(ObjectGuid vendor, ObjectGuid item, SellResult reason)
    {
        ServerPacket packet = new(Opcode.SMSG_SELL_ITEM, 24);
        GossipPackets.WriteSellFailed(packet.Body, vendor, item, reason);

        connection.Send(packet);
    }

    /// <summary>Whether an NPC has the flag a gossip line needs. A line with no flag is always shown.</summary>
    /// <remarks>
    /// This is what makes the shared menu 0 work: its options are "browse your goods", "train me",
    /// "make this inn your home", and each appears only on an NPC that really does that.
    /// </remarks>
    private static bool Offers(Creature npc, GossipMenuOption option) =>
        option.NpcFlagRequired == 0 || (npc.NpcFlags & option.NpcFlagRequired) != 0;

    /// <summary>The creature behind a guid, if it is one, alive, and close enough to talk to.</summary>
    private Creature? FindInteractable(ObjectGuid guid)
    {
        if (_player is null || _map is null || _map.Find(guid) is not Creature npc || !npc.IsAlive)
        {
            return null;
        }

        if (_player.Position.GetExactDist2dSq(npc.Position)
            > Map.InteractionDistance * Map.InteractionDistance)
        {
            return null;
        }

        return npc;
    }

    /// <summary>Where one of this player's items is, found by its guid.</summary>
    private (ItemPosition Position, Item Item)? FindOwnedItem(ObjectGuid guid)
    {
        if (_player is null)
        {
            return null;
        }

        foreach ((ItemPosition position, Item item) in _player.Inventory.AllWithPositions)
        {
            if (item.Guid == guid)
            {
                return (position, item);
            }
        }

        return null;
    }

    /// <summary>
    /// Answers "what mark goes over this NPC's head?". <c>CMSG_QUESTGIVER_STATUS_QUERY</c>.
    /// </summary>
    /// <remarks>
    /// The client asks about every questgiver it can see, repeatedly, so this has to be cheap and
    /// has to answer even when the answer is "nothing".
    /// </remarks>
    private void HandleQuestGiverStatusQuery(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid))
        {
            return;
        }

        ObjectGuid guid = new(rawGuid);

        ServerPacket packet = new(Opcode.SMSG_QUESTGIVER_STATUS, 9);
        QuestPackets.WriteStatus(packet.Body, guid, QuestGiverStatusFor(guid));

        connection.Send(packet);
    }

    /// <summary>
    /// Opens a questgiver. <c>CMSG_QUESTGIVER_HELLO</c>.
    /// </summary>
    /// <remarks>
    /// One quest goes straight to its own window; several produce a menu. Upstream shows a gossip
    /// menu first when the NPC has one — there is no gossip yet, so this goes directly to the
    /// quests.
    /// </remarks>
    private void HandleQuestGiverHello(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || FindQuestGiver(new ObjectGuid(rawGuid)) is not { } npc)
        {
            return;
        }

        List<QuestMenuEntry> menu = [];
        QuestTemplate? only = null;

        foreach (uint questId in QuestsFor(npc))
        {
            if (!world.Quests.TryGet(questId, out QuestTemplate? quest) || quest is null)
            {
                continue;
            }

            uint icon = MenuIconFor(quest!);

            if (icon == QuestGiverStatus.None)
            {
                continue;
            }

            only = quest;
            menu.Add(new QuestMenuEntry(quest.Id, icon, quest.Level, quest.Flags, quest.LogTitle));
        }

        if (menu.Count == 1 && only is not null)
        {
            OpenQuest(npc.Guid, only);

            return;
        }

        ServerPacket packet = new(Opcode.SMSG_QUESTGIVER_QUEST_LIST, 128 + (menu.Count * 64));
        QuestPackets.WriteQuestList(packet.Body, npc.Guid, string.Empty, menu);

        connection.Send(packet);
    }

    /// <summary>Opens one quest from the menu. <c>CMSG_QUESTGIVER_QUERY_QUEST</c>.</summary>
    private void HandleQuestGiverQueryQuest(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || !reader.TryReadUInt32(out uint questId))
        {
            return;
        }

        if (world.Quests.TryGet(questId, out QuestTemplate? quest) && quest is not null)
        {
            OpenQuest(new ObjectGuid(rawGuid), quest!);
        }
    }

    /// <summary>Takes a quest. <c>CMSG_QUESTGIVER_ACCEPT_QUEST</c>.</summary>
    /// <remarks>
    /// The NPC is checked to actually offer it. Without that, a client can accept any quest in the
    /// game by naming its id at any NPC — the accept packet carries both.
    /// </remarks>
    private void HandleQuestGiverAcceptQuest(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || !reader.TryReadUInt32(out uint questId))
        {
            return;
        }

        if (FindQuestGiver(new ObjectGuid(rawGuid)) is not { } npc
            || !QuestsFor(npc).Contains(questId))
        {
            return;
        }

        if (!world.Quests.TryGet(questId, out QuestTemplate? quest) || quest is null)
        {
            return;
        }

        if (_player.Quests.Accept(quest) is not { } progress)
        {
            return;
        }

        Log.QuestAccepted(logger, _player.Name, quest.LogTitle, questId, connection.RemoteAddress);

        // A quest with no objectives is complete on acceptance, and the client needs telling or the
        // log entry never turns green.
        if (progress.Status == QuestStatus.Complete)
        {
            SendQuestComplete(questId);
        }
    }

    /// <summary>Asks to hand a quest in. <c>CMSG_QUESTGIVER_COMPLETE_QUEST</c>.</summary>
    private void HandleQuestGiverCompleteQuest(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || !reader.TryReadUInt32(out uint questId))
        {
            return;
        }

        if (!world.Quests.TryGet(questId, out QuestTemplate? quest) || quest is null)
        {
            return;
        }

        ObjectGuid npc = new(rawGuid);
        bool canComplete = _player.Quests.StatusOf(questId) == QuestStatus.Complete;

        // A quest with no item objectives skips the "have you got them?" window entirely — it would
        // have nothing to show, and the player would be looking at an empty dialog.
        if (quest.RequiredItemCount == 0 && canComplete)
        {
            SendOfferReward(npc, quest);

            return;
        }

        ServerPacket packet = new(Opcode.SMSG_QUESTGIVER_REQUEST_ITEMS, 256);
        QuestPackets.WriteRequestItems(packet.Body, npc, quest, canComplete, DisplayIdFor);

        connection.Send(packet);
    }

    /// <summary>
    /// Takes the reward. <c>CMSG_QUESTGIVER_CHOOSE_REWARD</c>.
    /// </summary>
    /// <remarks>
    /// Everything is paid here and nowhere else: the items, the money, the experience, and the
    /// removal of the required items. Splitting it across the complete and choose handlers is how
    /// a quest ends up paying twice.
    /// </remarks>
    private void HandleQuestGiverChooseReward(ReadOnlyMemory<byte> payload)
    {
        if (_player is null || _map is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt64(out ulong rawGuid) || !reader.TryReadUInt32(out uint questId)
            || !reader.TryReadUInt32(out uint choice))
        {
            return;
        }

        if (!world.Quests.TryGet(questId, out QuestTemplate? quest) || quest is null
            || _player.Quests.Find(questId) is not { } progress
            || progress.Status != QuestStatus.Complete)
        {
            return;
        }

        if (!GiveQuestRewards(quest, choice))
        {
            return;
        }

        TakeQuestRequirements(quest);

        _player.Quests.Reward(progress);

        uint experience = QuestReward.Experience(quest, _player.Level, world.Stores.QuestXp);
        uint money = quest.RewardMoney;

        _player.Money += money;

        if (experience > 0)
        {
            IReadOnlyList<LevelUp> levels = Experience.Give(
                _player, experience, world.ExperienceTable, world.Stats);

            SendExperienceGain(ObjectGuid.Empty, experience, levels);
        }

        ServerPacket packet = new(Opcode.SMSG_QUESTGIVER_QUEST_COMPLETE, 24);
        QuestPackets.WriteComplete(packet.Body, questId, experience, money);

        connection.Send(packet);

        Log.QuestCompleted(logger, _player.Name, quest.LogTitle, questId, experience, connection.RemoteAddress);
    }

    /// <summary>Abandons a quest. <c>CMSG_QUESTLOG_REMOVE_QUEST</c>.</summary>
    private void HandleQuestLogRemoveQuest(ReadOnlyMemory<byte> payload)
    {
        if (_player is null)
        {
            return;
        }

        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt8(out byte slot))
        {
            return;
        }

        // The client names a slot, not a quest, so the quest has to be found by where it sits.
        foreach (QuestProgress progress in _player.Quests.All)
        {
            if (progress.Slot == slot)
            {
                _player.Quests.Abandon(progress.QuestId);

                return;
            }
        }
    }

    /// <summary>Shows a quest's details, or its hand-in window if it is already complete.</summary>
    private void OpenQuest(ObjectGuid npc, QuestTemplate quest)
    {
        if (_player is null)
        {
            return;
        }

        if (_player.Quests.StatusOf(quest.Id) == QuestStatus.Complete)
        {
            SendOfferReward(npc, quest);

            return;
        }

        ServerPacket packet = new(Opcode.SMSG_QUESTGIVER_QUEST_DETAILS, 512);

        QuestPackets.WriteDetails(
            packet.Body, npc, quest, quest.RewardMoney,
            QuestReward.Experience(quest, _player.Level, world.Stores.QuestXp), DisplayIdFor);

        connection.Send(packet);
    }

    private void SendOfferReward(ObjectGuid npc, QuestTemplate quest)
    {
        if (_player is null)
        {
            return;
        }

        ServerPacket packet = new(Opcode.SMSG_QUESTGIVER_OFFER_REWARD, 384);

        QuestPackets.WriteOfferReward(
            packet.Body, npc, quest, quest.RewardMoney,
            QuestReward.Experience(quest, _player.Level, world.Stores.QuestXp), DisplayIdFor);

        connection.Send(packet);
    }

    /// <summary>
    /// Hands over a quest's items.
    /// </summary>
    /// <remarks>
    /// Nothing is given if not everything fits. A half-paid quest is worse than an unpaid one: the
    /// quest is gone from the log and the player cannot get the rest.
    /// </remarks>
    private bool GiveQuestRewards(QuestTemplate quest, uint choice)
    {
        if (_player is null)
        {
            return false;
        }

        List<(ItemTemplate Template, uint Count)> giving = [];

        foreach (QuestItem reward in quest.Rewards)
        {
            if (reward.IsUsed && world.Items.TryGet(reward.ItemId, out ItemTemplate? template) && template is not null)
            {
                giving.Add((template, reward.Count));
            }
        }

        if (quest.RewardChoiceCount > 0)
        {
            QuestItem[] used = [.. quest.RewardChoices.Where(item => item.IsUsed)];

            // The client sends an index into the *used* choices, not into the six columns.
            if (choice >= used.Length)
            {
                return false;
            }

            if (world.Items.TryGet(used[choice].ItemId, out ItemTemplate? chosen) && chosen is not null)
            {
                giving.Add((chosen, used[choice].Count));
            }
        }

        foreach ((ItemTemplate template, uint count) in giving)
        {
            if (_player.Inventory.CanStore(template, count, out _) != InventoryResult.Ok)
            {
                SendEquipError(InventoryResult.InventoryFull);

                return false;
            }
        }

        foreach ((ItemTemplate template, uint count) in giving)
        {
            _player.Inventory.Store(template, count, itemGuids.Next, out _);
        }

        return true;
    }

    /// <summary>Takes back what the quest asked for — the items, and the money.</summary>
    private void TakeQuestRequirements(QuestTemplate quest)
    {
        if (_player is null)
        {
            return;
        }

        foreach (QuestItem required in quest.RequiredItems)
        {
            if (required.IsUsed)
            {
                RemoveItems(required.ItemId, required.Count);
            }
        }

        _player.Money -= Math.Min(_player.Money, quest.RequiredMoney);
    }

    /// <summary>Takes a number of an item out of the bags, across as many stacks as it takes.</summary>
    private void RemoveItems(uint entry, uint count)
    {
        if (_player is null)
        {
            return;
        }

        uint remaining = count;

        foreach ((ItemPosition position, Item item) in _player.Inventory.AllWithPositions.ToList())
        {
            if (remaining == 0)
            {
                break;
            }

            if (item.Entry != entry)
            {
                continue;
            }

            uint taken = Math.Min(item.Count, remaining);

            _player.Inventory.Destroy(position, taken, out Item? removed);
            remaining -= taken;

            if (removed is not null)
            {
                _knownItems.Remove(removed.Guid);
                _pendingUpdates.AddOutOfRange(removed.Guid);
            }
        }
    }

    /// <summary>Which mark goes over an NPC, from this player's point of view.</summary>
    private uint QuestGiverStatusFor(ObjectGuid guid)
    {
        if (_player is null || FindQuestGiver(guid) is not { } npc)
        {
            return QuestGiverStatus.None;
        }

        uint best = QuestGiverStatus.None;

        // A quest ready to hand in wins over one waiting to be taken: the question mark is the more
        // urgent of the two, and an NPC showing an exclamation mark over a finished quest sends the
        // player looking for something to do.
        foreach (uint questId in world.QuestEnders.For(npc.Entry))
        {
            if (_player.Quests.StatusOf(questId) == QuestStatus.Complete)
            {
                return QuestGiverStatus.Reward;
            }

            if (_player.Quests.StatusOf(questId) == QuestStatus.Incomplete)
            {
                best = QuestGiverStatus.Incomplete;
            }
        }

        foreach (uint questId in world.QuestStarters.For(npc.Entry))
        {
            if (world.Quests.TryGet(questId, out QuestTemplate? quest) && quest is not null
                && _player.Quests.CanTake(quest) == QuestTakeResult.Ok)
            {
                return QuestGiverStatus.Available;
            }
        }

        return best;
    }

    /// <summary>The icon one quest gets in the menu, or None if it should not be listed.</summary>
    private uint MenuIconFor(QuestTemplate quest)
    {
        if (_player is null)
        {
            return QuestGiverStatus.None;
        }

        return _player.Quests.StatusOf(quest.Id) switch
        {
            QuestStatus.Complete => QuestGiverStatus.Reward,
            QuestStatus.Incomplete => QuestGiverStatus.Incomplete,
            _ => _player.Quests.CanTake(quest) == QuestTakeResult.Ok
                ? QuestGiverStatus.Available
                : QuestGiverStatus.None,
        };
    }

    /// <summary>Every quest an NPC is involved in, starter and ender alike.</summary>
    private List<uint> QuestsFor(Creature npc)
    {
        List<uint> quests = [.. world.QuestStarters.For(npc.Entry)];

        foreach (uint questId in world.QuestEnders.For(npc.Entry))
        {
            if (!quests.Contains(questId))
            {
                quests.Add(questId);
            }
        }

        return quests;
    }

    /// <summary>The creature behind a guid, if it is one, alive, and a questgiver in range.</summary>
    private Creature? FindQuestGiver(ObjectGuid guid)
    {
        if (_player is null || _map is null || _map.Find(guid) is not Creature npc)
        {
            return null;
        }

        if (!npc.IsAlive || (npc.NpcFlags & NpcFlagQuestGiver) == 0)
        {
            return null;
        }

        return npc;
    }

    private uint DisplayIdFor(uint itemId) =>
        world.Items.TryGet(itemId, out ItemTemplate? template) && template is not null ? template.DisplayId : 0;

    /// <summary>Tells this client one quest objective has moved.</summary>
    public void SendQuestKillCredit(
        uint questId, uint wireEntry, uint current, uint required, ObjectGuid victim)
    {
        ServerPacket packet = new(Opcode.SMSG_QUESTUPDATE_ADD_KILL, 28);
        QuestPackets.WriteAddKill(packet.Body, questId, wireEntry, current, required, victim);

        connection.Send(packet);
    }

    /// <summary>Tells this client every objective on a quest is now met.</summary>
    public void SendQuestComplete(uint questId)
    {
        ServerPacket packet = new(Opcode.SMSG_QUESTUPDATE_COMPLETE, 4);
        QuestPackets.WriteQuestComplete(packet.Body, questId);

        connection.Send(packet);
    }

    /// <summary>Runs one move and reports whatever went wrong.</summary>
    private void Move(ItemPosition from, ItemPosition to)
    {
        if (_player is null || from == to)
        {
            return;
        }

        Item? moving = _player.Inventory.Get(from);
        InventoryResult result = _player.Inventory.Swap(from, to);

        if (result != InventoryResult.Ok)
        {
            SendEquipError(result, moving?.Guid ?? default, moving?.Template.RequiredLevel ?? 0);
        }
    }

    /// <summary>The first empty slot in a container, or null when it is full.</summary>
    private ItemPosition? FirstFreeSlotIn(byte bag)
    {
        if (_player is null)
        {
            return null;
        }

        if (bag == InventorySlots.Backpack)
        {
            for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
            {
                if (_player.Inventory.Get(bag, slot) is null)
                {
                    return new ItemPosition(bag, slot);
                }
            }

            return null;
        }

        if (_player.Inventory.Get(InventorySlots.Backpack, bag) is not Bag container)
        {
            return null;
        }

        for (byte slot = 0; slot < container.SlotCount; slot++)
        {
            if (_player.Inventory.Get(bag, slot) is null)
            {
                return new ItemPosition(bag, slot);
            }
        }

        return null;
    }

    /// <summary>Tells this client why an inventory operation was refused.</summary>
    private void SendEquipError(InventoryResult result, ObjectGuid item = default, uint requiredLevel = 0)
    {
        ServerPacket packet = new(Opcode.SMSG_INVENTORY_CHANGE_FAILURE, 24);
        InventoryChangeFailure.Write(packet.Body, (byte)result, item, requiredLevel: requiredLevel);

        connection.Send(packet);
    }

    /// <summary>The cooldown figures an item's spell slot falls back on. See <see cref="SpellCooldownLookup"/>.</summary>
    private bool TryGetSpellCooldown(
        int spellId, out uint recoveryMs, out uint category, out uint categoryRecoveryMs)
    {
        recoveryMs = 0;
        category = 0;
        categoryRecoveryMs = 0;

        if (spellId <= 0 || !world.Spells.Spells.TryGet((uint)spellId, out SpellEntry spell))
        {
            return false;
        }

        recoveryMs = spell.RecoveryTime;
        category = spell.Category;
        categoryRecoveryMs = spell.CategoryRecoveryTime;

        return true;
    }

    private void HandleMovement(Opcode opcode, ReadOnlyMemory<byte> payload)
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

        // The floor lookup is handed in rather than reached for, so the validator stays a pure
        // function and a map with no collision data simply skips the check.
        MovementVerdict verdict = MovementValidator.Validate(
            _player.Position, claimed, elapsed, _map.GetFloor);

        if (!verdict.Accepted)
        {
            Log.MovementRejected(
                logger, _player.Name, verdict.Rejection.ToString(), verdict.Detail ?? "", connection.RemoteAddress);

            // The client is told where the server thinks it is, so an honest client that drifted
            // snaps back instead of desynchronising silently.
            SendKnownPosition();
            return;
        }

        _lastMovementMs = now;
        _player.Movement.CopyFrom(claimed);

        // The map owns position: moving cells is what keeps visibility queries correct.
        _map.Relocate(_player, _player.Movement.Position);

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
        _map.BroadcastMovement(_player, opcode, _player.Movement);
    }

    /// <summary>
    /// Corrects a client whose movement was refused.
    /// </summary>
    /// <remarks>
    /// A teleport acknowledgement is how the protocol says "you are actually here". Without it a
    /// rejected client keeps walking in its own reality and every later packet is rejected too.
    /// </remarks>
    private void SendKnownPosition()
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

        connection.Send(packet);
    }

    // ------------------------------------------------------------------ IPlayerConnection

    /// <summary>Adds an object's create block to this tick's batch.</summary>
    /// <remarks>
    /// Players and creatures produce the same block: upstream gives both
    /// <c>UPDATEFLAG_LIVING | UPDATEFLAG_STATIONARY_POSITION</c> in the <c>Unit</c> constructor, and
    /// what differs is the type id and the update type, both of which the builder derives.
    /// <para>
    /// A gameobject is a different block entirely — no movement, no speeds, a packed rotation, and
    /// a field block that ends at slot 18 rather than 148. Nothing in a create block carries a
    /// length, so sending one as the other is not a degraded picture; it is a disconnect.
    /// </para>
    /// </remarks>
    public void QueueCreate(WorldObject other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other is GameObject gameObject)
        {
            _pendingUpdates.AddBlock(UpdateBlockBuilder.BuildGameObjectCreateBlock(
                gameObject.Guid, gameObject.Fields, gameObject.Position, gameObject.PackedRotation));
        }
        else
        {
            other.SyncMovement();

            _pendingUpdates.AddBlock(UpdateBlockBuilder.BuildCreateBlock(
                other.Guid, other.TypeId, other.Fields, other.Movement, other.Speeds, isSelf: false));
        }

        Log.ObjectBecameVisible(logger, other.Name, _player?.Name ?? "?");
    }

    /// <summary>
    /// Adds this player's own changed fields, and its items', to the batch.
    /// </summary>
    /// <remarks>
    /// A values block, not a create: the client already has the object and is being told what
    /// moved. Without this an item can change hands entirely server-side — the slot guid is
    /// written, the stack count is updated — and the client's bag never changes.
    /// <para>
    /// <b>To this client only.</b> Half of a player's fields are marked private upstream, and the
    /// dirty mask does not know the difference — so sending the same block to onlookers would hand
    /// them a stranger's bags. Other players therefore do not yet see each other's health, level or
    /// equipment change; that needs the per-viewer mask filtering that
    /// <c>UpdateFieldFlags</c> exists for, and is recorded as a gap.
    /// </para>
    /// </remarks>
    private void QueueOwnChanges()
    {
        if (_player is null)
        {
            return;
        }

        if (_player.Fields.IsDirty)
        {
            _pendingUpdates.AddBlock(UpdateBlockBuilder.BuildValuesBlock(_player.Guid, _player.Fields));
            _player.Fields.ClearDirty();
        }

        foreach (Item item in _player.Inventory.All)
        {
            if (!item.Fields.IsDirty)
            {
                continue;
            }

            // An item the client has never been told about needs a create, not a values block —
            // a split makes one mid-session, and a values block for an unknown guid is dropped.
            _pendingUpdates.AddBlock(_knownItems.Add(item.Guid)
                ? UpdateBlockBuilder.BuildItemCreateBlock(item.Guid, item.TypeId, item.Fields)
                : UpdateBlockBuilder.BuildValuesBlock(item.Guid, item.Fields));

            item.Fields.ClearDirty();
        }
    }

    /// <summary>Item guids this client has been sent a create block for.</summary>
    private readonly HashSet<ObjectGuid> _knownItems = [];

    /// <summary>Adds an object's destroy to this tick's batch.</summary>
    /// <remarks>
    /// The out-of-range block is how the client is told to destroy its copy; there is no separate
    /// destroy opcode in 3.3.5a.
    /// </remarks>
    public void QueueDestroy(ObjectGuid objectGuid) => _pendingUpdates.AddOutOfRange(objectGuid);

    /// <summary>
    /// Sends everything queued this tick as one packet.
    /// </summary>
    /// <remarks>
    /// One <c>SMSG_UPDATE_OBJECT</c> can carry any number of blocks, and the client reads them all.
    /// Before this existed, entering the world at the human starting point produced 131 packets —
    /// one per creature in sight — where upstream sends one.
    /// </remarks>
    public void FlushUpdates()
    {
        QueueOwnChanges();

        if (_pendingUpdates.BlockCount > 0)
        {
            byte[] payload = _pendingUpdates.BuildPayload();
            _pendingUpdates = new UpdateData();

            bool compressed = UpdateData.TryCompress(payload, out byte[] body);

            ServerPacket packet = new(
                compressed ? Opcode.SMSG_COMPRESSED_UPDATE_OBJECT : Opcode.SMSG_UPDATE_OBJECT,
                body.Length);
            packet.Body.WriteBytes(body);

            connection.Send(packet);
        }

        // After the update packet, never before: a move for an object the client has not been told
        // about is silently dropped, and the creature then appears frozen until its next move.
        foreach ((ObjectGuid mover, CreatureMove move, uint splineId) in _pendingMonsterMoves)
        {
            ServerPacket packet = new(Opcode.SMSG_MONSTER_MOVE, 64);

            MonsterMove.Write(
                packet.Body, mover, move.Start, move.Destination, splineId, move.DurationMs);

            connection.Send(packet);
        }

        _pendingMonsterMoves.Clear();

        // Swings go last, for the same reason moves do — a swing naming a creature the client has
        // not been told about draws nothing, and the fight starts with an invisible attacker.
        foreach (AttackerState swing in _pendingMeleeSwings)
        {
            ServerPacket packet = new(Opcode.SMSG_ATTACKERSTATEUPDATE, 96);
            AttackerStateUpdate.Write(packet.Body, swing);

            connection.Send(packet);
        }

        _pendingMeleeSwings.Clear();

        // After the swings, so that a fight's melee and spell log lines arrive in the order the
        // tick produced them rather than grouped by kind.
        foreach (SpellDamageLog damage in _pendingSpellDamage)
        {
            ServerPacket packet = new(Opcode.SMSG_SPELLNONMELEEDAMAGELOG, 64);
            SpellDamageLogPacket.Write(packet.Body, damage);

            connection.Send(packet);
        }

        _pendingSpellDamage.Clear();

        // Last, with the rest of the combat log. A tick is a log line like any other and belongs in
        // tick order behind the swings and spells of the same update.
        foreach (PeriodicAuraLog tick in _pendingAuraLogs)
        {
            ServerPacket packet = new(Opcode.SMSG_PERIODICAURALOG, 48);

            AuraUpdate.WritePeriodicLog(
                packet.Body,
                tick.Target,
                tick.Caster,
                tick.SpellId,
                tick.AuraType,
                tick.Amount,
                tick.Overflow,
                tick.SchoolMask);

            connection.Send(packet);
        }

        _pendingAuraLogs.Clear();
    }

    /// <summary>
    /// Tells this client that a creature has started walking somewhere.
    /// </summary>
    /// <remarks>
    /// <c>SMSG_MONSTER_MOVE</c> is its own opcode and cannot travel inside an update packet, so it
    /// is held until the flush and sent immediately after — which is what keeps it behind the create
    /// block for the same creature.
    /// </remarks>
    public void QueueMonsterMove(ObjectGuid mover, CreatureMove move, uint splineId) =>
        _pendingMonsterMoves.Add((mover, move, splineId));

    /// <summary>Tells this client about one melee swing.</summary>
    /// <remarks>
    /// The one place the game layer's <see cref="MeleeDamageInfo"/> becomes wire bytes. The hit-info
    /// bits pass straight through, which is what makes the packet's conditional trailers line up
    /// with the flags the combat code actually set.
    /// </remarks>
    public void QueueMeleeSwing(
        ObjectGuid attacker, ObjectGuid target, MeleeDamageInfo info, uint targetHealthBeforeHit) =>
        _pendingMeleeSwings.Add(new AttackerState(
            HitInfo: (uint)info.HitInfo,
            Attacker: attacker,
            Target: target,
            Damage: info.Damage,
            TargetHealth: targetHealthBeforeHit,
            VictimState: (byte)info.VictimState,
            Blocked: info.BlockedAmount));

    /// <summary>Tells this client to start or stop drawing an attack animation.</summary>
    /// <remarks>
    /// The two opcodes disagree about guid encoding — start sends them whole, stop packs them. That
    /// is upstream's inconsistency, reproduced rather than tidied, because the client reads each one
    /// the way it was written.
    /// </remarks>
    public void SendAttackState(ObjectGuid attacker, ObjectGuid? victim, bool attacking, bool victimIsDead)
    {
        if (attacking)
        {
            if (victim is not { } target)
            {
                return;
            }

            ServerPacket start = new(Opcode.SMSG_ATTACKSTART, 16);
            AttackerStateUpdate.WriteAttackStart(start.Body, attacker, target);

            connection.Send(start);
            return;
        }

        ServerPacket stop = new(Opcode.SMSG_ATTACKSTOP, 24);
        AttackerStateUpdate.WriteAttackStop(stop.Body, attacker, victim, victimIsDead);

        connection.Send(stop);
    }

    /// <summary>Tells this client it has died.</summary>
    public void SendPlayerDied(int reclaimDelayMs)
    {
        ServerPacket packet = new(Opcode.SMSG_CORPSE_RECLAIM_DELAY, 4);
        packet.Body.WriteUInt32((uint)Math.Max(reclaimDelayMs / 1000, 0));

        connection.Send(packet);
    }

    /// <summary>Tells this client where its spirit healer is.</summary>
    public void SendSpiritHealerLocation(uint mapId, Position at)
    {
        ServerPacket packet = new(Opcode.SMSG_DEATH_RELEASE_LOC, 16);
        packet.Body.WriteUInt32(mapId);
        packet.Body.WriteSingle(at.X);
        packet.Body.WriteSingle(at.Y);
        packet.Body.WriteSingle(at.Z);

        connection.Send(packet);
    }

    /// <summary>Tells this client it is alive again.</summary>
    /// <remarks>
    /// The minimap marker is cleared with a map id of <c>-1</c> and no coordinates that mean
    /// anything — there is no separate "forget the spirit healer" opcode.
    /// </remarks>
    public void SendResurrected() =>
        SendSpiritHealerLocation(uint.MaxValue, default);

    /// <summary>Tells this client it gained experience, and about any levels that came with it.</summary>
    /// <remarks>
    /// Immediate and in order: the experience log first, then a banner per level. Sending the banner
    /// first shows a level-up with nothing having caused it.
    /// </remarks>
    public void SendExperienceGain(ObjectGuid victim, uint amount, IReadOnlyList<LevelUp> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        ServerPacket gain = new(Opcode.SMSG_LOG_XPGAIN, 24);
        ExperiencePackets.WriteLogXpGain(gain.Body, victim, amount);

        connection.Send(gain);

        foreach (LevelUp level in levels)
        {
            ServerPacket banner = new(Opcode.SMSG_LEVELUP_INFO, 56);

            // Only the mana slot is ever non-zero. Rage and energy do not grow with level, so a
            // delta for them would animate a change that did not happen.
            int[] powerDeltas = [level.ManaDelta, 0, 0, 0, 0, 0];

            ExperiencePackets.WriteLevelUp(
                banner.Body, level.NewLevel, level.HealthDelta, powerDeltas, level.StatDeltas);

            connection.Send(banner);
        }
    }

    /// <summary>Tells this client about one spell's damage.</summary>
    /// <remarks>
    /// The one place the game layer's <see cref="SpellHit"/> becomes wire bytes, mirroring how a
    /// melee swing is encoded — the layer that decided the damage never names a field order.
    /// </remarks>
    public void QueueSpellDamage(
        ObjectGuid target, ObjectGuid caster, uint spellId, SpellHit hit, uint targetHealthBeforeHit) =>
        _pendingSpellDamage.Add(new SpellDamageLog(
            Target: target,
            Attacker: caster,
            SpellId: spellId,
            Damage: hit.Damage,
            TargetHealth: targetHealthBeforeHit,
            SchoolMask: hit.SchoolMask,
            Resisted: hit.Resisted,
            Blocked: hit.Blocked,
            IsPhysical: hit.IsPhysical));

    /// <summary>Tells this client an aura has landed, or that one already there has changed.</summary>
    public void SendAuraApplied(
        ObjectGuid target,
        byte slot,
        uint spellId,
        byte flags,
        byte casterLevel,
        byte stackAmount,
        ObjectGuid caster,
        int maxDurationMs,
        int remainingMs)
    {
        ServerPacket packet = new(Opcode.SMSG_AURA_UPDATE, 32);

        AuraUpdate.WriteApplied(
            packet.Body,
            target,
            new AuraSlotUpdate(
                Slot: slot,
                SpellId: spellId,
                Flags: flags,
                CasterLevel: casterLevel,
                StackAmount: stackAmount,
                Caster: caster,
                MaxDurationMs: maxDurationMs,
                RemainingMs: remainingMs));

        connection.Send(packet);
    }

    /// <summary>Tells this client an aura is gone.</summary>
    public void SendAuraRemoved(ObjectGuid target, byte slot)
    {
        ServerPacket packet = new(Opcode.SMSG_AURA_UPDATE, 16);
        AuraUpdate.WriteRemoved(packet.Body, target, slot);

        connection.Send(packet);
    }

    /// <summary>Queues one periodic aura tick for the combat log.</summary>
    public void QueuePeriodicAuraLog(
        ObjectGuid target,
        ObjectGuid caster,
        uint spellId,
        uint auraType,
        uint amount,
        uint overflow,
        uint schoolMask) =>
        _pendingAuraLogs.Add(new PeriodicAuraLog(
            target, caster, spellId, auraType, amount, overflow, schoolMask));

    /// <summary>Tells this client that a cast has started.</summary>
    /// <remarks>
    /// The target block is rebuilt from the guid rather than kept from the incoming packet: the
    /// client sends flags this server does not act on, and echoing them back would promise blocks
    /// the writer does not produce.
    /// </remarks>
    public void SendSpellStart(
        ObjectGuid caster, uint spellId, byte castCount, int castTimeMs, ObjectGuid target, uint powerLeft)
    {
        ServerPacket packet = new(Opcode.SMSG_SPELL_START, 64);

        SpellCast.WriteSpellStart(
            packet.Body,
            caster,
            castCount,
            spellId,
            SpellCastFlags.HasTrajectory | SpellCastFlags.PowerLeftSelf,
            castTimeMs,
            TargetsFor(target),
            powerLeft);

        connection.Send(packet);
    }

    /// <summary>Tells this client that a cast landed.</summary>
    public void SendSpellGo(
        ObjectGuid caster, uint spellId, byte castCount, ObjectGuid target, uint powerLeft)
    {
        ServerPacket packet = new(Opcode.SMSG_SPELL_GO, 96);

        // One hit and no misses: nothing here can miss yet, so a miss list would be structure with
        // no way to reach it. A self-cast hits the caster, which is what an empty target means.
        ObjectGuid[] hits = [target.IsEmpty ? caster : target];

        SpellCast.WriteSpellGo(
            packet.Body,
            caster,
            castCount,
            spellId,
            SpellCastFlags.Unknown9 | SpellCastFlags.PowerLeftSelf,
            MsTime.Now,
            hits,
            [],
            TargetsFor(target),
            powerLeft);

        connection.Send(packet);
    }

    /// <summary>Tells this client its cast was refused.</summary>
    public void SendCastFailed(byte castCount, uint spellId, SpellCastResult result)
    {
        ServerPacket packet = new(Opcode.SMSG_CAST_FAILED, 8);
        SpellCast.WriteCastFailed(packet.Body, castCount, spellId, result);

        connection.Send(packet);
    }

    private static SpellCastTargets TargetsFor(ObjectGuid target) =>
        target.IsEmpty
            ? SpellCastTargets.Self
            : new SpellCastTargets(SpellCastTargetFlags.Unit, ObjectTarget: target);

    /// <summary>Tells this client why its swing did not land.</summary>
    /// <remarks>
    /// Both packets are bodiless — the opcode is the whole message. Suppressed to one per run of
    /// failures by <see cref="_lastSwingError"/>, because the swing retries every 100 ms and the
    /// client prints the message every time it is told.
    /// </remarks>
    public void SendSwingError(SwingError reason)
    {
        if (reason == _lastSwingError)
        {
            return;
        }

        _lastSwingError = reason;

        // `None` is how a landed swing clears the suppression, so the next failure is reported
        // again. Nothing goes on the wire for it — there is no "your swing worked" packet.
        if (reason == SwingError.None)
        {
            return;
        }

        Opcode opcode = reason switch
        {
            SwingError.NotInRange => Opcode.SMSG_ATTACKSWING_NOTINRANGE,
            SwingError.BadFacing => Opcode.SMSG_ATTACKSWING_BADFACING,
            _ => Opcode.SMSG_ATTACKSWING_NOTINRANGE,
        };

        connection.Send(new ServerPacket(opcode, 0));
    }

    /// <summary>Relays another player's movement to this client.</summary>
    public void SendMovement(Opcode opcode, ObjectGuid mover, MovementInfo movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        ServerPacket packet = new(opcode, 64);
        packet.Body.WritePackedGuid(mover);
        movement.WriteTo(packet.Body);

        connection.Send(packet);
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
        connection.Send(response);

        await SavePlayerAsync(cancellationToken).ConfigureAwait(true);

        ServerPacket complete = new(Opcode.SMSG_LOGOUT_COMPLETE, 0);
        connection.Send(complete);

        if (_player is not null)
        {
            if (_map is not null)
            {
                _map.Remove(_player);
            }

            Log.PlayerLeftWorld(logger, _player.Name, connection.RemoteAddress);
        }

        _player = null;
        _map = null;

        // Back to character selection: the opcode table stops admitting gameplay opcodes again.
        Status = SessionStatus.Authed;
    }

    private void HandleLogoutCancel()
    {
        ServerPacket packet = new(Opcode.SMSG_LOGOUT_CANCEL_ACK, 0);
        connection.Send(packet);
    }

    /// <summary>
    /// Writes the player's position back to the database.
    /// </summary>
    /// <remarks>
    /// Called on logout and on disconnect. A client that vanishes mid-session — alt-F4, a dropped
    /// connection — must not lose its progress, and there is no tick loop yet to save periodically.
    /// </remarks>
    /// <summary>
    /// Saves and detaches the character, from whichever thread the connection died on.
    /// </summary>
    /// <remarks>
    /// A dropped connection is noticed by the read task, which is not the world loop — and
    /// <see cref="SavePlayerAsync"/> takes the player off its map, which is map state. So the work
    /// is posted to the loop rather than run where it was noticed. Awaiting the posted task is what
    /// keeps the host from disposing the connection out from under it.
    /// </remarks>
    public Task DisconnectAsync()
    {
        if (_scheduler is null)
        {
            return SavePlayerAsync(CancellationToken.None);
        }

        return _scheduler.Factory.StartNew(
            () => SavePlayerAsync(CancellationToken.None),
            CancellationToken.None).Unwrap();
    }

    public async Task SavePlayerAsync(CancellationToken cancellationToken)
    {
        // Captured once, and used for the rest of the method. The field is nulled when the player
        // leaves the world, and there is an await in the middle of this — so re-reading it after
        // the save would throw whenever a logout and a dropped connection overlap, which is exactly
        // what happens when a client disconnects immediately after logging out.
        Player? player = _player;

        if (player is null)
        {
            return;
        }

        await characters.SavePositionAsync(
            player.Guid.Counter,
            player.MapId,
            player.ZoneId,
            player.Position.X,
            player.Position.Y,
            player.Position.Z,
            player.Position.Orientation,
            cancellationToken).ConfigureAwait(true);

        await inventory
            .SaveAsync(player.Guid.Counter, Snapshot(player), cancellationToken)
            .ConfigureAwait(true);

        await inventory
            .SaveQuestsAsync(player.Guid.Counter, QuestSnapshot(player), cancellationToken)
            .ConfigureAwait(true);

        Log.PlayerSaved(logger, player.Name, player.Position.X, player.Position.Y);

        // A dropped connection never sends a logout, so the map has to be told here too or the
        // player stays visible to everyone else as a statue.
        if (_map is not null)
        {
            _map.Remove(player);
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
            await characters.ListForAccountAsync(_account.Id, cancellationToken).ConfigureAwait(true);

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

        // Before the create block, and it has to be: the block carries the slot guids, and the item
        // objects have to exist for the client to be told about them in the same packet.
        await LoadInventoryAsync(player, cancellationToken).ConfigureAwait(true);

        // After the inventory, and it has to be: a collection quest's progress is recounted from
        // the bags, and counting before they are filled marks every one of them unstarted.
        await LoadQuestsAsync(player, cancellationToken).ConfigureAwait(true);

        _player = player;
        Status = SessionStatus.LoggedIn;

        PlayerLogin.SendLoginSequence(connection, player, options.Motd, SendAccountDataTimes);
        _knownItems.Clear();

        foreach (ObjectGuid itemGuid in PlayerLogin.SendSelfCreate(connection, player))
        {
            _knownItems.Add(itemGuid);
        }

        PlayerLogin.SendTimeSyncRequest(connection, 0);

        // Added after the self create: the client needs to know about itself before it is told
        // about anyone standing next to it.
        player.Connection = this;
        _map = maps.GetMap(player.MapId);
        _map.Add(player);

        Log.PlayerEnteredWorld(
            logger, player.Name, player.MapId, player.Position.X, player.Position.Y, connection.RemoteAddress);
    }

    /// <summary>
    /// What one character is wearing, for the selection screen.
    /// </summary>
    /// <remarks>
    /// Read straight from the database rather than through an <see cref="Inventory"/>: the
    /// character is not logged in, there is no <see cref="Player"/>, and building one per row of
    /// the list to read three fields off it would be the expensive way round.
    /// <para>
    /// Only rows in the player's own array and only equipment slots count — something in the
    /// backpack is not worn, and a bag guid on the wrong row would place a chestpiece on the head.
    /// </para>
    /// </remarks>
    private async Task<CharacterList.VisibleItem[]> VisibleEquipmentAsync(
        uint characterId, CancellationToken cancellationToken)
    {
        CharacterList.VisibleItem[] worn = new CharacterList.VisibleItem[CharacterList.EquipmentSlots];

        IReadOnlyList<StoredItem> stored = await inventory
            .LoadAsync(characterId, cancellationToken)
            .ConfigureAwait(true);

        foreach (StoredItem row in stored)
        {
            if (row.BagId != 0 || row.Slot >= InventorySlots.EquipmentEnd)
            {
                continue;
            }

            if (world.Items.TryGet(row.Entry, out ItemTemplate? template) && template is not null)
            {
                worn[row.Slot] = new CharacterList.VisibleItem(template.DisplayId, template.InventoryType);
            }
        }

        return worn;
    }

    /// <summary>
    /// Dresses a character that has just been created and writes its things to the database.
    /// </summary>
    /// <remarks>
    /// A real <see cref="Player"/> is built for this rather than the gear being computed from the
    /// race and class alone, because <c>StoreInBestSlots</c> asks the player questions — its level,
    /// whether it can dual wield, which slots are taken. Building one is also what upstream does:
    /// <c>Player::Create</c> dresses the object and then saves it.
    /// </remarks>
    private async Task GiveStartingGearAsync(CharacterEntity created, CancellationToken cancellationToken)
    {
        CharacterSummary summary = new(
            created.Id, created.Name, created.Race, created.Class, created.Gender,
            created.Skin, created.Face, created.HairStyle, created.HairColor, created.FacialStyle,
            created.Level, created.Zone, created.Map,
            created.PositionX, created.PositionY, created.PositionZ,
            created.GuildId, created.PlayerFlags, created.AtLoginFlags);

        if (!world.TryBuildPlayer(summary, out Player? player, out string? reason))
        {
            // Not fatal: the character exists and can be played, just empty-handed. Logging it is
            // the only way anyone would ever find out.
            Log.StartingGearSkipped(logger, created.Name, reason ?? "unknown");

            return;
        }

        int placed = world.ApplyStartingGear(player, itemGuids.Next);

        if (placed == 0)
        {
            Log.StartingGearSkipped(logger, created.Name, "no outfit for this race, class and gender");

            return;
        }

        await inventory
            .SaveAsync(created.Id, Snapshot(player), cancellationToken)
            .ConfigureAwait(true);

        Log.StartingGearGiven(logger, created.Name, placed);
    }

    /// <summary>
    /// Rebuilds a character's inventory from the database.
    /// </summary>
    /// <remarks>
    /// Rows arrive bags-first, and are placed with <c>Restore</c> rather than through the equip
    /// rules: what was legal when the item was put there is what it stays. Re-checking on load
    /// would quietly rearrange a player's bags whenever a rule changed.
    /// <para>
    /// A row whose template has since vanished is dropped. It cannot be built, and leaving the
    /// slot's guid pointing at nothing gives the client an item it can never draw.
    /// </para>
    /// </remarks>
    private async Task LoadInventoryAsync(Player player, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredItem> stored = await inventory
            .LoadAsync(player.Guid.Counter, cancellationToken)
            .ConfigureAwait(true);

        int missing = 0;

        foreach (StoredItem row in stored)
        {
            if (!world.Items.TryGet(row.Entry, out ItemTemplate? template) || template is null)
            {
                missing++;
                continue;
            }

            Item item = Item.Create(row.ItemId, template, player.Guid);

            item.Count = row.Count;
            item.Durability = row.Durability;
            item.DurationSeconds = row.DurationSeconds;
            item.ItemFlags = row.Flags;

            for (int i = 0; i < row.SpellCharges.Length && i < ItemConstants.MaxSpells; i++)
            {
                item.SetSpellCharges(i, row.SpellCharges[i]);
            }

            // The stored bag is an item guid; the inventory addresses bags by the slot they are
            // worn in, so it is translated here rather than storing something that moves.
            player.Inventory.Restore(PositionFor(player, row), item);
        }

        if (missing > 0)
        {
            Log.InventoryRowsDropped(logger, player.Name, missing);
        }
    }

    /// <summary>Turns a stored row's bag guid back into the bag slot the inventory addresses.</summary>
    private static ItemPosition PositionFor(Player player, in StoredItem row)
    {
        if (row.BagId == 0)
        {
            return new ItemPosition(InventorySlots.Backpack, row.Slot);
        }

        for (byte bagSlot = InventorySlots.BagStart; bagSlot < InventorySlots.BagEnd; bagSlot++)
        {
            if (player.Inventory.Get(InventorySlots.Backpack, bagSlot) is Bag bag
                && bag.Guid.Counter == row.BagId)
            {
                return new ItemPosition(bagSlot, row.Slot);
            }
        }

        // The bag is gone. Falling back to the player's own array would overwrite whatever is in
        // that slot, so the item goes nowhere — a slot of 255 is out of range and is ignored.
        return new ItemPosition(InventorySlots.Backpack, InventorySlots.None);
    }

    /// <summary>
    /// Rebuilds a character's quest log from the database.
    /// </summary>
    /// <remarks>
    /// Kill counters come back from their columns; <b>item counters are recounted from the bags</b>
    /// rather than stored. An item can arrive by looting, trading, buying or mail, and a stored
    /// count is one missed increment away from a quest that can never be finished.
    /// </remarks>
    private async Task LoadQuestsAsync(Player player, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredQuest> stored = await inventory
            .LoadQuestsAsync(player.Guid.Counter, cancellationToken)
            .ConfigureAwait(true);

        foreach (StoredQuest row in stored)
        {
            if (!world.Quests.TryGet(row.QuestId, out QuestTemplate? quest) || quest is null)
            {
                continue;
            }

            QuestProgress progress = new(row.QuestId)
            {
                Status = (QuestStatus)row.Status,
                Slot = row.Slot,
            };

            for (int i = 0; i < row.Killed.Length && i < progress.Killed.Length; i++)
            {
                progress.Killed[i] = row.Killed[i];
            }

            player.Quests.Restore(progress);

            if (progress.Status == QuestStatus.Incomplete)
            {
                player.Quests.RecountItems(quest, progress);
            }
        }
    }

    /// <summary>What a character's quest log holds, in the shape the database stores.</summary>
    private static List<StoredQuest> QuestSnapshot(Player player)
    {
        List<StoredQuest> rows = [];

        foreach (QuestProgress progress in player.Quests.All)
        {
            rows.Add(new StoredQuest(
                progress.QuestId,
                (byte)progress.Status,
                progress.Slot,
                [.. progress.Killed]));
        }

        return rows;
    }

    /// <summary>What a character is carrying, in the shape the database stores.</summary>
    private static List<StoredItem> Snapshot(Player player)
    {
        List<StoredItem> rows = [];

        foreach ((ItemPosition position, Item item) in player.Inventory.AllWithPositions)
        {
            int[] charges = new int[ItemConstants.MaxSpells];

            for (int i = 0; i < charges.Length; i++)
            {
                charges[i] = item.GetSpellCharges(i);
            }

            // The bag is written as its own guid rather than the slot it is worn in: moving a bag
            // between bag slots would otherwise mean rewriting every row inside it.
            uint bagId = position.IsOnThePlayer
                ? 0
                : player.Inventory.Equipped(position.Bag)?.Guid.Counter ?? 0;

            rows.Add(new StoredItem(
                ItemId: item.Guid.Counter,
                Entry: item.Entry,
                Count: item.Count,
                Durability: item.Durability,
                DurationSeconds: item.DurationSeconds,
                SpellCharges: charges,
                Flags: item.ItemFlags,
                BagId: bagId,
                Slot: position.Slot));
        }

        return rows;
    }

    private void HandleRealmSplit(ReadOnlyMemory<byte> payload)
    {
        PacketReader reader = new(payload.Span);
        reader.TryReadUInt32(out uint token);

        ServerPacket packet = new(Opcode.SMSG_REALM_SPLIT, 16);
        packet.Body.WriteUInt32(token);
        packet.Body.WriteUInt32(0);              // 0 normal, 1 split, 2 split pending
        packet.Body.WriteCString("01/01/01");

        connection.Send(packet);
    }

    private bool HandlePing(ReadOnlyMemory<byte> payload)
    {
        PacketReader reader = new(payload.Span);

        if (!reader.TryReadUInt32(out uint ping))
        {
            return false;
        }

        ServerPacket packet = new(Opcode.SMSG_PONG, 4);
        packet.Body.WriteUInt32(ping);

        connection.Send(packet);
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
