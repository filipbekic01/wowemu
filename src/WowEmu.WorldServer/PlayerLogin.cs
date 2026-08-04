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
/// </remarks>
public static class PlayerLogin
{
    /// <summary>Account-data slots that are per character rather than account-wide.</summary>
    public const uint PerCharacterCacheMask = 0xEA;

    /// <summary>Seconds per game-time unit. The client scales its clock by this.</summary>
    public const float GameSpeed = 0.01666667f;

    /// <summary>Sends everything that precedes the player appearing.</summary>
    public static async Task SendLoginSequenceAsync(
        WorldConnection connection,
        Player player,
        string motd,
        Func<uint, CancellationToken, Task> sendAccountDataTimes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(sendAccountDataTimes);

        await SendVerifyWorldAsync(connection, player, cancellationToken).ConfigureAwait(false);

        // Per-character slots this time, not the account-wide mask sent before character selection.
        await sendAccountDataTimes(PerCharacterCacheMask, cancellationToken).ConfigureAwait(false);

        await SendFeatureSystemStatusAsync(connection, cancellationToken).ConfigureAwait(false);
        await SendMotdAsync(connection, motd, cancellationToken).ConfigureAwait(false);
        await SendLearnedDanceMovesAsync(connection, cancellationToken).ConfigureAwait(false);

        // Before add-to-map.
        await SendBindPointAsync(connection, player, cancellationToken).ConfigureAwait(false);
        await SendInstanceDifficultyAsync(connection, cancellationToken).ConfigureAwait(false);
        await SendTimeSpeedAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the client which map and where on it. This is what ends the loading screen.
    /// </summary>
    public static async Task SendVerifyWorldAsync(
        WorldConnection connection,
        Player player,
        CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_LOGIN_VERIFY_WORLD, 20);

        packet.Body.WriteUInt32(player.MapId);
        packet.Body.WriteSingle(player.Position.X);
        packet.Body.WriteSingle(player.Position.Y);
        packet.Body.WriteSingle(player.Position.Z);
        packet.Body.WriteSingle(player.Position.Orientation);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendFeatureSystemStatusAsync(
        WorldConnection connection,
        CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_FEATURE_SYSTEM_STATUS, 2);

        packet.Body.WriteUInt8(2);   // complaint system: enabled with auto-ignore
        packet.Body.WriteUInt8(0);   // voice chat off

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendMotdAsync(
        WorldConnection connection,
        string motd,
        CancellationToken cancellationToken)
    {
        string[] lines = motd.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        ServerPacket packet = new(Opcode.SMSG_MOTD, 64);
        packet.Body.WriteUInt32((uint)lines.Length);

        foreach (string line in lines)
        {
            packet.Body.WriteCString(line);
        }

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendLearnedDanceMovesAsync(
        WorldConnection connection,
        CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_LEARNED_DANCE_MOVES, 8);

        packet.Body.WriteUInt32(0);
        packet.Body.WriteUInt32(0);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Where the character resurrects and hearthstones to.</summary>
    private static async Task SendBindPointAsync(
        WorldConnection connection,
        Player player,
        CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_BINDPOINTUPDATE, 20);

        // No homebind table yet, so the bind point is where the character stands. Phase 5's
        // persistence work gives this its own storage.
        packet.Body.WriteSingle(player.Position.X);
        packet.Body.WriteSingle(player.Position.Y);
        packet.Body.WriteSingle(player.Position.Z);
        packet.Body.WriteUInt32(player.MapId);
        packet.Body.WriteUInt32(player.ZoneId);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendInstanceDifficultyAsync(
        WorldConnection connection,
        CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_INSTANCE_DIFFICULTY, 8);

        packet.Body.WriteUInt32(0);   // normal
        packet.Body.WriteUInt32(0);   // not a dynamic-difficulty raid

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronises the client's clock and calendar.</summary>
    private static async Task SendTimeSpeedAsync(
        WorldConnection connection,
        CancellationToken cancellationToken)
    {
        ServerPacket packet = new(Opcode.SMSG_LOGIN_SETTIMESPEED, 12);

        packet.Body.WritePackedTime(DateTime.Now);
        packet.Body.WriteSingle(GameSpeed);
        packet.Body.WriteUInt32(0);   // added in 3.1.2

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the player's own create block — the packet that makes the character exist.
    /// </summary>
    /// <remarks>
    /// Compressed above 100 bytes, which a player always is: a create block carries every non-zero
    /// field and there are a lot of them. The opcode changes with the compression, so the two are
    /// decided together.
    /// </remarks>
    public static async Task SendSelfCreateAsync(
        WorldConnection connection,
        Player player,
        CancellationToken cancellationToken)
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

        byte[] payload = update.BuildPayload();
        bool compressed = UpdateData.TryCompress(payload, out byte[] body);

        ServerPacket packet = new(
            compressed ? Opcode.SMSG_COMPRESSED_UPDATE_OBJECT : Opcode.SMSG_UPDATE_OBJECT,
            body.Length);

        packet.Body.WriteBytes(body);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);

        // The client now has everything it was sent; anything further is a change from here.
        player.Fields.ClearDirty();
    }

    /// <summary>Asks the client to report its clock, which it does periodically from then on.</summary>
    public static async Task SendTimeSyncRequestAsync(
        WorldConnection connection,
        uint counter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ServerPacket packet = new(Opcode.SMSG_TIME_SYNC_REQ, 4);
        packet.Body.WriteUInt32(counter);

        await connection.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }
}
