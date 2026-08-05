using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;

namespace WowEmu.Game.Combat;

/// <summary>Where a ghost is sent, and what the client draws on the minimap.</summary>
/// <param name="MapId">The graveyard's map.</param>
/// <param name="Position">Where the spirit healer stands.</param>
/// <param name="Name">For the log; the client never sees it.</param>
public readonly record struct Graveyard(uint MapId, Position Position, string Name);

/// <summary>
/// Dying, releasing, and coming back.
/// </summary>
/// <remarks>
/// Port of <c>Player::KillPlayer</c>, <c>BuildPlayerRepop</c>, <c>RepopAtGraveyard</c> and
/// <c>ResurrectPlayer</c> — the parts that do not need auras, pets or battlegrounds.
/// <para>
/// <b>The ghost has 1 health, not 0.</b> A player at zero is a corpse the client will not let you
/// move; releasing sets health to 1 and that is what turns the body into something that walks. The
/// two states are distinct and the client draws them differently.
/// </para>
/// </remarks>
public static class PlayerDeath
{
    /// <summary>How long a corpse lies before the client offers to release for you. Six minutes.</summary>
    public const int ReleaseTimerMs = 6 * 60 * 1000;

    /// <summary>The health a ghost walks around with.</summary>
    public const uint GhostHealth = 1;

    /// <summary>How much health and mana resurrecting at a corpse restores.</summary>
    /// <remarks>Half, which is upstream's figure for a corpse run.</remarks>
    public const float ResurrectFraction = 0.5f;

    /// <summary><c>PLAYER_FLAGS_GHOST</c>. The client draws the wisp from this.</summary>
    public const uint PlayerFlagGhost = 0x0010;

    /// <summary>Alliance's graveyard faction id in <c>game_graveyard_zone</c>.</summary>
    public const uint AllianceFaction = GraveyardZone.FactionAlliance;

    /// <summary>Horde's.</summary>
    public const uint HordeFaction = GraveyardZone.FactionHorde;

    /// <summary>
    /// Kills a player.
    /// </summary>
    /// <remarks>
    /// Health goes to zero and the state to <see cref="DeathState.Corpse"/> — the same promotion a
    /// creature makes, and for the same reason: <see cref="DeathState.JustDied"/> is the moment
    /// things that happen exactly once happen, not a state anything updates in.
    /// <para>
    /// The corpse is <i>not</i> created here. Upstream is explicit that a player might still be
    /// falling when it dies, and a corpse placed mid-fall lands in the air.
    /// </para>
    /// </remarks>
    public static void Kill(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!player.IsAlive)
        {
            return;
        }

        player.DeathState = DeathState.JustDied;

        player.Health = 0;
        player.SetPower(Unit.PowerRage, 0);

        player.AttackStop();
        player.IsInCombat = false;
        player.Casting.Cancel();

        // Everything that hated it forgets. A creature still holding a dead player on its threat
        // list keeps standing over the corpse instead of going home.
        player.Threat.Clear();

        player.DeathState = DeathState.Corpse;
        player.ReleaseTimerMs = ReleaseTimerMs;
    }

    /// <summary>
    /// Releases the spirit: the body becomes a ghost.
    /// </summary>
    /// <remarks>
    /// Sets the ghost flag and one point of health. Health is what the client keys movement off —
    /// leave it at zero and the player is stuck on the spot with the release button gone.
    /// </remarks>
    /// <returns>Whether there was a corpse to release from.</returns>
    public static bool Release(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (player.DeathState != DeathState.Corpse || player.IsGhost)
        {
            return false;
        }

        player.CorpsePosition = player.Position;
        player.CorpseMapId = player.MapId;

        player.IsGhost = true;
        player.Health = GhostHealth;
        player.ReleaseTimerMs = 0;

        return true;
    }

    /// <summary>
    /// Brings a player back at half health and mana.
    /// </summary>
    /// <remarks>
    /// Rage is emptied rather than halved — it is earned in combat, and starting a fight with half a
    /// bar you did not fight for is not the same resource.
    /// </remarks>
    public static void Resurrect(Player player, float fraction = ResurrectFraction)
    {
        ArgumentNullException.ThrowIfNull(player);

        player.DeathState = DeathState.Alive;
        player.IsGhost = false;
        player.ReleaseTimerMs = 0;

        player.Health = Math.Max((uint)(player.MaxHealth * fraction), 1);

        player.SetPower(Unit.PowerMana, (uint)(player.GetMaxPower(Unit.PowerMana) * fraction));
        player.SetPower(Unit.PowerEnergy, (uint)(player.GetMaxPower(Unit.PowerEnergy) * fraction));
        player.SetPower(Unit.PowerRage, 0);

        player.CorpseMapId = null;
    }

    /// <summary>
    /// The closest graveyard a player may release to.
    /// </summary>
    /// <remarks>
    /// Zone first, then distance. A zone usually lists one graveyard per faction plus a neutral
    /// fallback, so the faction filter runs before the distance comparison — taking the nearest and
    /// then checking the faction can leave a player with nowhere to go in a contested zone.
    /// <para>
    /// <b>Graveyards on another map are skipped.</b> Several zones list one, and teleporting across
    /// maps needs a transfer the session cannot do yet — releasing to a graveyard you cannot reach
    /// is worse than releasing to a further one you can.
    /// </para>
    /// </remarks>
    /// <returns>The graveyard, or null when the zone has none this player can use.</returns>
    public static Graveyard? ClosestGraveyard(
        Player player,
        GraveyardStore graveyards,
        DbcStore<WorldSafeLocsEntry> locations)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(graveyards);
        ArgumentNullException.ThrowIfNull(locations);

        uint faction = player.IsAlliance ? AllianceFaction : HordeFaction;

        Graveyard? best = null;
        float bestDistance = float.MaxValue;

        foreach (GraveyardZone link in graveyards.ForZone(player.ZoneId))
        {
            if (!link.AllowsFaction(faction))
            {
                continue;
            }

            if (!locations.TryGet(link.GraveyardId, out WorldSafeLocsEntry location)
                || location.MapId != player.MapId)
            {
                continue;
            }

            Position at = new(location.X, location.Y, location.Z, 0f);
            float distance = player.Position.GetExactDist2dSq(at);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = new Graveyard(location.MapId, at, location.Name);
            }
        }

        return best;
    }
}
