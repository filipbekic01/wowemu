using WowEmu.Core;
using WowEmu.Game;
using WowEmu.Network;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>
/// The packet burst the client expects after <c>CMSG_PLAYER_LOGIN</c>.
/// </summary>
/// <remarks>
/// Port of <c>HandlePlayerLoginOpcode</c> and <c>Player::SendInitialPacketsBeforeAddToMap</c>.
/// <para>
/// <b>Order matters.</b> The client drives its loading screen off this sequence and waits for each
/// step; a missing packet leaves it sitting at a black screen with no error. The sequence below is
/// upstream's, minus the parts that need systems no phase has built — guild info, reputations,
/// action buttons, spells, auras.
/// </para>
/// <para>
/// Nothing here awaits. Sending queues a packet and returns, so the order the client sees is the
/// order these are called in — which is the only thing that ever mattered about the sequence.
/// </para>
/// </remarks>
public static class PlayerLogin
{
    /// <summary>Account-data slots that are per character rather than account-wide.</summary>
    public const uint PerCharacterCacheMask = 0xEA;

    /// <summary>Seconds per game-time unit. The client scales its clock by this.</summary>
    public const float GameSpeed = 0.01666667f;

    /// <summary>Sends everything that precedes the player appearing.</summary>
    public static void SendLoginSequence(
        WorldConnection connection,
        Player player,
        string motd,
        Action<uint> sendAccountDataTimes)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(sendAccountDataTimes);

        SendVerifyWorld(connection, player);

        // Per-character slots this time, not the account-wide mask sent before character selection.
        sendAccountDataTimes(PerCharacterCacheMask);

        SendFeatureSystemStatus(connection);
        SendMotd(connection, motd);
        SendLearnedDanceMoves(connection);

        // Before add-to-map.
        SendBindPoint(connection, player);
        SendInstanceDifficulty(connection);
        SendTimeSpeed(connection);

        // Last of the burst, and before the create block. The client builds its spellbook and
        // action bars from this and nothing else — without it a character knows nothing it can
        // cast, whatever the server thinks.
        SendInitialSpells(connection, player);
        SendActionButtons(connection, player);
    }

    /// <summary>
    /// Tells the client which map and where on it. This is what ends the loading screen.
    /// </summary>
    public static void SendVerifyWorld(WorldConnection connection, Player player)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(player);

        ServerPacket packet = new(Opcode.SMSG_LOGIN_VERIFY_WORLD, 20);

        packet.Body.WriteUInt32(player.MapId);
        packet.Body.WriteSingle(player.Position.X);
        packet.Body.WriteSingle(player.Position.Y);
        packet.Body.WriteSingle(player.Position.Z);
        packet.Body.WriteSingle(player.Position.Orientation);

        connection.Send(packet);
    }

    private static void SendFeatureSystemStatus(WorldConnection connection)
    {
        ServerPacket packet = new(Opcode.SMSG_FEATURE_SYSTEM_STATUS, 2);

        packet.Body.WriteUInt8(2);   // complaint system: enabled with auto-ignore
        packet.Body.WriteUInt8(0);   // voice chat off

        connection.Send(packet);
    }

    private static void SendMotd(WorldConnection connection, string motd)
    {
        string[] lines = motd.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        ServerPacket packet = new(Opcode.SMSG_MOTD, 64);
        packet.Body.WriteUInt32((uint)lines.Length);

        foreach (string line in lines)
        {
            packet.Body.WriteCString(line);
        }

        connection.Send(packet);
    }

    private static void SendLearnedDanceMoves(WorldConnection connection)
    {
        ServerPacket packet = new(Opcode.SMSG_LEARNED_DANCE_MOVES, 8);

        packet.Body.WriteUInt32(0);
        packet.Body.WriteUInt32(0);

        connection.Send(packet);
    }

    /// <summary>Where the character resurrects and hearthstones to.</summary>
    private static void SendBindPoint(WorldConnection connection, Player player)
    {
        ServerPacket packet = new(Opcode.SMSG_BINDPOINTUPDATE, 20);

        // No homebind table yet, so the bind point is where the character stands. Phase 5's
        // persistence work gives this its own storage.
        packet.Body.WriteSingle(player.Position.X);
        packet.Body.WriteSingle(player.Position.Y);
        packet.Body.WriteSingle(player.Position.Z);
        packet.Body.WriteUInt32(player.MapId);
        packet.Body.WriteUInt32(player.ZoneId);

        connection.Send(packet);
    }

    private static void SendInstanceDifficulty(WorldConnection connection)
    {
        ServerPacket packet = new(Opcode.SMSG_INSTANCE_DIFFICULTY, 8);

        packet.Body.WriteUInt32(0);   // normal
        packet.Body.WriteUInt32(0);   // not a dynamic-difficulty raid

        connection.Send(packet);
    }

    /// <summary>Synchronises the client's clock and calendar.</summary>
    private static void SendTimeSpeed(WorldConnection connection)
    {
        ServerPacket packet = new(Opcode.SMSG_LOGIN_SETTIMESPEED, 12);

        packet.Body.WritePackedTime(DateTime.Now);
        packet.Body.WriteSingle(GameSpeed);
        packet.Body.WriteUInt32(0);   // added in 3.1.2

        connection.Send(packet);
    }

    /// <summary>Sends the whole spellbook in one packet.</summary>
    private static void SendInitialSpells(WorldConnection connection, Player player)
    {
        ServerPacket packet = new(Opcode.SMSG_INITIAL_SPELLS, 8 + (player.Spells.Count * 6));
        InitialSpells.Write(packet.Body, [.. player.Spells.Known]);

        connection.Send(packet);
    }

    /// <summary>Sends the action bars.</summary>
    /// <remarks>
    /// All 144 buttons go out, empty ones included — the client reads them positionally.
    /// </remarks>
    private static void SendActionButtons(WorldConnection connection, Player player)
    {
        ServerPacket packet = new(Opcode.SMSG_ACTION_BUTTONS, 1 + (ActionButtons.MaxButtons * 4));
        ActionButtons.Write(packet.Body, player.Actions.Buttons);

        connection.Send(packet);
    }

    /// <summary>
    /// Sends the player's own create block — the packet that makes the character exist.
    /// </summary>
    /// <remarks>
    /// Compressed above 100 bytes, which a player always is: a create block carries every non-zero
    /// field and there are a lot of them. The opcode changes with the compression, so the two are
    /// decided together.
    /// </remarks>
    /// <returns>The item guids the client has now been told about.</returns>
    public static IReadOnlyCollection<ObjectGuid> SendSelfCreate(WorldConnection connection, Player player)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(player);

        player.SyncMovement();

        UpdateData update = new();
        update.AddBlock(UpdateBlockBuilder.BuildCreateBlock(
            player.Guid,
            player.TypeId,
            player.Fields,
            player.Movement,
            player.Speeds,
            isSelf: true));

        // The items go in the same packet, after the player. Their guids are already in the
        // player's slot fields, and a client holding a guid it has no object for draws an empty
        // bag slot — the item is there and invisible.
        List<ObjectGuid> sent = [];

        foreach (Item item in player.Inventory.All)
        {
            update.AddBlock(UpdateBlockBuilder.BuildItemCreateBlock(item.Guid, item.TypeId, item.Fields));
            item.Fields.ClearDirty();
            sent.Add(item.Guid);
        }

        byte[] payload = update.BuildPayload();
        bool compressed = UpdateData.TryCompress(payload, out byte[] body);

        ServerPacket packet = new(
            compressed ? Opcode.SMSG_COMPRESSED_UPDATE_OBJECT : Opcode.SMSG_UPDATE_OBJECT,
            body.Length);

        packet.Body.WriteBytes(body);

        connection.Send(packet);

        // The client now has everything it was sent; anything further is a change from here.
        player.Fields.ClearDirty();

        return sent;
    }

    /// <summary>Asks the client to report its clock, which it does periodically from then on.</summary>
    public static void SendTimeSyncRequest(WorldConnection connection, uint counter)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ServerPacket packet = new(Opcode.SMSG_TIME_SYNC_REQ, 4);
        packet.Body.WriteUInt32(counter);

        connection.Send(packet);
    }
}
