using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using Xunit.Abstractions;

// The test namespace ends in `Unit`, which shadows the class of the same name.
using GameUnit = WowEmu.Game.Unit;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Dying, releasing, and coming back.
/// </summary>
/// <remarks>
/// The three states a player passes through are not two: alive, a corpse that cannot move, and a
/// ghost that can. The client draws each differently and keys movement off the health value, so
/// conflating any pair leaves someone stuck.
/// </remarks>
public sealed class PlayerDeathTests
{
    private static Player Dead()
    {
        Player player = ExperienceFixture.NewPlayer(level: 5);
        player.MaxHealth = 200;
        player.Health = 200;

        PlayerDeath.Kill(player);
        return player;
    }

    [Fact]
    public void Dying_LandsOnCorpseNotOnJustDied()
    {
        Player player = Dead();

        Assert.Equal(DeathState.Corpse, player.DeathState);
        Assert.False(player.IsAlive);
        Assert.Equal(0u, player.Health);
    }

    /// <summary>A corpse is not yet a ghost — it has not released.</summary>
    [Fact]
    public void ACorpse_IsNotYetAGhost()
    {
        Player player = Dead();

        Assert.False(player.IsGhost);
        Assert.Equal(PlayerDeath.ReleaseTimerMs, player.ReleaseTimerMs);
    }

    [Fact]
    public void Dying_StopsEverythingItWasDoing()
    {
        Player player = ExperienceFixture.NewPlayer(level: 5);
        player.MaxHealth = 200;
        player.Health = 200;

        Creature enemy = CreatureFixture.Build();

        player.Attack(enemy);
        player.IsInCombat = true;
        player.Threat.AddThreat(enemy, 50f);

        PlayerDeath.Kill(player);

        Assert.Null(player.Victim);
        Assert.False(player.IsInCombat);
        Assert.True(player.Threat.IsEmpty);
        Assert.Equal(0u, player.GetPower(GameUnit.PowerRage));
    }

    [Fact]
    public void KillingAnAlreadyDeadPlayer_ChangesNothing()
    {
        Player player = Dead();
        player.ReleaseTimerMs = 1234;

        PlayerDeath.Kill(player);

        Assert.Equal(1234, player.ReleaseTimerMs);
    }

    // ------------------------------------------------------------------ releasing

    /// <summary>
    /// Releasing gives the ghost one point of health, not zero.
    /// </summary>
    /// <remarks>
    /// The client keys movement off the health value. Leaving it at zero after release produces a
    /// ghost that cannot walk and has lost the button that would have fixed it.
    /// </remarks>
    [Fact]
    public void Releasing_GivesTheGhostOnePointOfHealth()
    {
        Player player = Dead();

        Assert.True(PlayerDeath.Release(player));

        Assert.True(player.IsGhost);
        Assert.Equal(PlayerDeath.GhostHealth, player.Health);
        Assert.Equal(1u, player.Health);
        Assert.Equal(0, player.ReleaseTimerMs);
    }

    /// <summary>
    /// The corpse's position is remembered before anything moves.
    /// </summary>
    /// <remarks>
    /// It is what a corpse run walks back to. Losing it leaves a permanent ghost with nowhere to
    /// resurrect.
    /// </remarks>
    [Fact]
    public void Releasing_RemembersWhereTheBodyIs()
    {
        Player player = Dead();

        player.Position = new Position(-8900f, -120f, 84f, 0f);
        player.MapId = 0;

        PlayerDeath.Release(player);

        Assert.Equal(0u, player.CorpseMapId);
        Assert.Equal(-8900f, player.CorpsePosition.X, 0.01f);
        Assert.Equal(-120f, player.CorpsePosition.Y, 0.01f);
    }

    [Fact]
    public void ALivingPlayer_HasNoSpiritToRelease()
    {
        Player player = ExperienceFixture.NewPlayer(level: 5);

        Assert.False(PlayerDeath.Release(player));
        Assert.False(player.IsGhost);
    }

    [Fact]
    public void ReleasingTwice_DoesNothingTheSecondTime()
    {
        Player player = Dead();

        Assert.True(PlayerDeath.Release(player));
        Assert.False(PlayerDeath.Release(player));
    }

    // ------------------------------------------------------------------ resurrecting

    [Fact]
    public void Resurrecting_RestoresHalfHealthAndMana()
    {
        Player player = Dead();

        player.MaxHealth = 200;
        player.SetMaxPower(GameUnit.PowerMana, 100);

        PlayerDeath.Release(player);
        PlayerDeath.Resurrect(player);

        Assert.True(player.IsAlive);
        Assert.False(player.IsGhost);
        Assert.Equal(100u, player.Health);
        Assert.Equal(50u, player.GetPower(GameUnit.PowerMana));
    }

    /// <summary>
    /// Rage is emptied rather than halved.
    /// </summary>
    /// <remarks>
    /// It is earned in combat. Starting a fight with half a bar you did not fight for is a different
    /// resource, and it is the reason rage is not simply scaled with everything else.
    /// </remarks>
    [Fact]
    public void Resurrecting_EmptiesRageRatherThanHalvingIt()
    {
        Player player = Dead();

        player.SetMaxPower(GameUnit.PowerRage, 1000);
        player.SetPower(GameUnit.PowerRage, 1000);

        PlayerDeath.Release(player);
        PlayerDeath.Resurrect(player);

        Assert.Equal(0u, player.GetPower(GameUnit.PowerRage));
    }

    /// <summary>Resurrecting never leaves a player at zero health, however small the fraction.</summary>
    [Fact]
    public void Resurrecting_NeverLeavesZeroHealth()
    {
        Player player = Dead();
        player.MaxHealth = 10;

        PlayerDeath.Release(player);
        PlayerDeath.Resurrect(player, fraction: 0.001f);

        Assert.True(player.Health > 0, "resurrected straight back into a corpse");
    }

    [Fact]
    public void Resurrecting_ForgetsTheCorpse()
    {
        Player player = Dead();

        PlayerDeath.Release(player);
        Assert.NotNull(player.CorpseMapId);

        PlayerDeath.Resurrect(player);
        Assert.Null(player.CorpseMapId);
    }
}

/// <summary>Choosing a graveyard.</summary>
public sealed class GraveyardTests(ITestOutputHelper output)
{
    private static (GraveyardStore Zones, DbcStore<WorldSafeLocsEntry> Locations) Load()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        GraveyardStore zones = new();

        zones.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None).GetAwaiter().GetResult();

        return (zones, stores.WorldSafeLocs);
    }

    /// <summary>
    /// The coordinates come from the DBC, not from a world table.
    /// </summary>
    /// <remarks>
    /// Newer AzerothCore reads them from <c>game_graveyard</c>; the vendored dump predates that and
    /// carries only the zone mapping. Same divergence as <c>creature_template_model</c> — the data
    /// is what this follows.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheCoordinates_ComeFromWorldSafeLocs()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.True(stores.WorldSafeLocs.Count > 600, $"only {stores.WorldSafeLocs.Count} safe locations");

        // Row 1 is Stormwind's, on map 0. If the format string were off, the map would not be a
        // small number and the coordinates would not be in Elwynn.
        Assert.True(stores.WorldSafeLocs.TryGet(1, out WorldSafeLocsEntry stormwind));

        Assert.Equal(0u, stormwind.MapId);
        Assert.Equal("Stormwind", stormwind.Name);
        Assert.Equal(-9115f, stormwind.X, 1f);
        Assert.Equal(423f, stormwind.Y, 1f);

        output.WriteLine(
            $"{stores.WorldSafeLocs.Count} safe locations; row 1 is " +
            $"'{stormwind.Name}' on map {stormwind.MapId} at ({stormwind.X:F0}, {stormwind.Y:F0}, {stormwind.Z:F0})");
    }

    [RequiresWorldDatabaseFact]
    public async Task TheZoneMapping_Loads()
    {
        GraveyardStore zones = new();
        await zones.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        Assert.True(zones.Count > 600, $"only {zones.Count} links");
        Assert.True(zones.ZoneCount > 100, $"only {zones.ZoneCount} zones");

        output.WriteLine($"{zones.Count} links across {zones.ZoneCount} zones");
    }

    /// <summary>
    /// A neutral graveyard is open to both sides; a factioned one is not.
    /// </summary>
    /// <remarks>
    /// 570 of the 700 rows are neutral, so the faction filter is the exception rather than the rule
    /// — which is exactly why forgetting it looks like it works.
    /// </remarks>
    [Fact]
    public void TheFactionFilter_LetsNeutralGraveyardsThrough()
    {
        GraveyardZone neutral = new(1, 12, GraveyardZone.FactionAny);
        GraveyardZone alliance = new(2, 12, GraveyardZone.FactionAlliance);

        Assert.True(neutral.AllowsFaction(GraveyardZone.FactionAlliance));
        Assert.True(neutral.AllowsFaction(GraveyardZone.FactionHorde));

        Assert.True(alliance.AllowsFaction(GraveyardZone.FactionAlliance));
        Assert.False(alliance.AllowsFaction(GraveyardZone.FactionHorde));
    }

    /// <summary>A human dying in Elwynn gets an Elwynn graveyard on the same map.</summary>
    [RequiresClientDataFact]
    public void AHumanDyingInElwynn_GetsAnElwynnGraveyard()
    {
        (GraveyardStore zones, DbcStore<WorldSafeLocsEntry> locations) = Load();

        Player player = ExperienceFixture.NewPlayer(level: 5);
        player.MapId = 0;
        player.ZoneId = 12;   // Elwynn Forest
        player.Position = new Position(-8949.95f, -132.493f, 83.5f, 0f);

        Graveyard? found = PlayerDeath.ClosestGraveyard(player, zones, locations);

        Assert.NotNull(found);
        Assert.Equal(0u, found!.Value.MapId);

        float distance = MathF.Sqrt(player.Position.GetExactDist2dSq(found.Value.Position));

        // Somewhere in the same corner of the world, not across the continent.
        Assert.True(distance < 2000f, $"nearest graveyard is {distance:F0} yards away");

        output.WriteLine($"'{found.Value.Name}' at {distance:F0} yards");
    }

    /// <summary>
    /// A graveyard on another map is skipped.
    /// </summary>
    /// <remarks>
    /// Several zones list one, and there is no cross-map transfer yet. Releasing to a graveyard the
    /// player cannot reach is worse than releasing to a further one it can.
    /// </remarks>
    [RequiresClientDataFact]
    public void AGraveyardOnAnotherMap_IsSkipped()
    {
        (GraveyardStore zones, DbcStore<WorldSafeLocsEntry> locations) = Load();

        Player player = ExperienceFixture.NewPlayer(level: 5);
        player.ZoneId = 12;
        player.MapId = 571;   // Northrend — nothing in Elwynn's list is on this map
        player.Position = new Position(0f, 0f, 0f, 0f);

        Assert.Null(PlayerDeath.ClosestGraveyard(player, zones, locations));
    }

    /// <summary>A zone with no graveyard at all returns nothing rather than throwing.</summary>
    [RequiresClientDataFact]
    public void AZoneWithNoGraveyard_ReturnsNothing()
    {
        (GraveyardStore zones, DbcStore<WorldSafeLocsEntry> locations) = Load();

        Player player = ExperienceFixture.NewPlayer(level: 5);
        player.ZoneId = 999_999;

        Assert.Null(PlayerDeath.ClosestGraveyard(player, zones, locations));
    }
}

/// <summary>Death driven through a real map.</summary>
public sealed class MapPlayerDeathTests
{
    /// <summary>
    /// A creature that kills a player stops fighting it and goes home.
    /// </summary>
    /// <remarks>
    /// Without this the creature stands over the corpse swinging at something the swing loop refuses
    /// to hit, forever — the player never releases, so nothing ever clears the threat list.
    /// </remarks>
    [Fact]
    public void WhenAPlayerDies_TheCreatureGivesUpAndGoesHome()
    {
        (Map map, Player player, Creature creature, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        creature.Threat.AddThreat(player, 100f);
        creature.Attack(player);

        player.Health = 1;
        map.KillPlayer(player);

        Assert.Equal(DeathState.Corpse, player.DeathState);
        Assert.Null(creature.Victim);
        Assert.False(creature.Threat.Contains(player));
        Assert.NotEmpty(link.Deaths);
        Assert.Equal(PlayerDeath.ReleaseTimerMs, link.Deaths[0]);
    }

    /// <summary>Damage that takes a player to zero kills it, the same way it kills a creature.</summary>
    [Fact]
    public void DamageThatEmptiesAPlayer_KillsIt()
    {
        (Map map, Player player, Creature creature, _) = MapCombatFixture.Engaged();

        player.MaxHealth = 5;
        player.Health = 5;
        creature.Threat.AddThreat(player, 1f);

        for (int i = 0; i < 200 && player.IsAlive; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.False(player.IsAlive);
        Assert.Equal(0u, player.Health);
    }

    /// <summary>Reclaiming needs the ghost to be at its corpse.</summary>
    [Fact]
    public void ReclaimingFromTooFarAway_IsRefused()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        map.KillPlayer(player);
        Assert.True(map.ReleaseSpirit(player));

        // Walked off somewhere else entirely.
        map.Relocate(player, new Position(
            player.CorpsePosition.X + 500f, player.CorpsePosition.Y, player.CorpsePosition.Z, 0f));

        Assert.False(map.ReclaimCorpse(player));
        Assert.True(player.IsGhost);
    }

    [Fact]
    public void ReclaimingAtTheCorpse_Resurrects()
    {
        (Map map, Player player, _, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        map.KillPlayer(player);
        map.ReleaseSpirit(player);

        map.Relocate(player, player.CorpsePosition);

        Assert.True(map.ReclaimCorpse(player));
        Assert.True(player.IsAlive);
        Assert.False(player.IsGhost);

        // The minimap marker is cleared with a map id of -1.
        Assert.Contains(link.SpiritHealers, marker => marker.MapId == uint.MaxValue);
    }

    /// <summary>A living player has nothing to reclaim.</summary>
    [Fact]
    public void ALivingPlayer_CannotReclaim()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        Assert.False(map.ReclaimCorpse(player));
    }

    /// <summary>
    /// With no graveyard data a ghost stays where it fell, rather than going nowhere useful.
    /// </summary>
    /// <remarks>
    /// It can still walk to its own corpse and reclaim, which is the difference between degraded
    /// and broken.
    /// </remarks>
    [Fact]
    public void WithNoGraveyardData_TheGhostStaysPut()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        Position died = player.Position;

        map.KillPlayer(player);

        Assert.True(map.ReleaseSpirit(player));
        Assert.True(player.IsGhost);
        Assert.Equal(died.X, player.Position.X, 0.01f);

        Assert.True(map.ReclaimCorpse(player));
    }
}
