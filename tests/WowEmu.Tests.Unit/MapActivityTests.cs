using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game;
using WowEmu.Game.Maps;
using WowEmu.Game.Movement;
using WowEmu.Data.Db;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Which creatures a tick actually updates.
/// </summary>
/// <remarks>
/// <para>
/// The map used to update every creature it had ever loaded, every tick. That makes the cost of a
/// tick a function of where players have <i>been</i> rather than where they <i>are</i>: walking
/// across a continent left thousands of creatures being ticked forever, each one running an aggro
/// scan looking for players hundreds of yards away. Measured on a real login, that was ~100 ms of a
/// 50 ms budget.
/// </para>
/// <para>
/// Upstream does not do this — AzerothCore updates the cells around its players. These tests pin
/// that behaviour, including the two cases where "near a player" is the wrong test: a creature in
/// combat, and a corpse waiting to respawn.
/// </para>
/// </remarks>
public sealed class MapActivityTests
{
    /// <summary>Comfortably outside any cell a player at the origin makes active.</summary>
    private const float FarAway = 2000f;

    /// <summary>Long enough that a wandering creature has finished waiting and picks a destination.</summary>
    private const uint LongEnoughToWander = RandomMovementGenerator.MaxWaitMs + 1;

    [Fact]
    public void ACreatureNextToAPlayer_IsTicked()
    {
        (Map map, _, Creature near, _) = Fixture();

        Assert.True(Wanders(map, near), "a creature beside a player should be wandering");
    }

    /// <summary>
    /// The point of the whole change: a creature nobody is near costs nothing.
    /// </summary>
    [Fact]
    public void ACreatureFarFromEveryPlayer_IsNotTicked()
    {
        (Map map, _, _, Creature far) = Fixture();

        Assert.False(Wanders(map, far), "a creature nobody is near should not be moving at all");
    }

    /// <summary>Both creatures are loaded and filed; only one of them is work.</summary>
    [Fact]
    public void ActiveCreatureCount_CountsOnlyWhatWasTicked()
    {
        (Map map, _, _, _) = Fixture();

        map.Update(gameplayDiff: LongEnoughToWander, sessionDiff: 0);

        Assert.Equal(2, map.CreatureCount);
        Assert.Equal(1, map.ActiveCreatureCount);
    }

    /// <summary>
    /// A creature that is fighting keeps running wherever it is standing.
    /// </summary>
    /// <remarks>
    /// Without this exemption a creature chased out of the active area would freeze mid-pursuit,
    /// still locked onto its victim, and never reach the evade that sends it home — so it would be
    /// waiting, still hostile, whenever somebody next walked past. Combat is rare, so exempting it
    /// costs nothing measurable.
    /// </remarks>
    [Fact]
    public void ACreatureInCombat_IsTickedEvenFarFromAnyPlayer()
    {
        (Map map, Player player, _, Creature far) = Fixture();

        // Baseline: only the creature beside the player is active.
        map.Update(gameplayDiff: LongEnoughToWander, sessionDiff: 0);
        Assert.Equal(1, map.ActiveCreatureCount);

        far.Attack(player);

        map.Update(gameplayDiff: LongEnoughToWander, sessionDiff: 0);
        Assert.Equal(2, map.ActiveCreatureCount);
    }

    /// <summary>
    /// A corpse in an empty zone still comes back.
    /// </summary>
    /// <remarks>
    /// Respawn is a timer comparison, not a range scan, so it is deliberately left ungated. Gating
    /// it would mean corpses respawn in front of the first player to walk up to them, which is
    /// exactly the artefact players notice.
    /// </remarks>
    [Fact]
    public void ADeadCreatureFarFromEveryPlayer_StillRespawns()
    {
        (Map map, _, _, Creature far) = Fixture();

        far.Kill();
        Assert.False(far.IsAlive);

        // Past the corpse delay and the respawn delay together.
        for (int tick = 0; tick < 20; tick++)
        {
            map.Update(gameplayDiff: 60_000, sessionDiff: 0);
        }

        Assert.True(far.IsAlive);
    }

    /// <summary>
    /// A player walking towards a creature makes it start running again.
    /// </summary>
    /// <remarks>
    /// The set is rebuilt every tick rather than maintained incrementally, so this is really a check
    /// that it is rebuilt at all — a set computed once at login would pass every other test here.
    /// </remarks>
    [Fact]
    public void WalkingTowardsACreature_MakesItActive()
    {
        (Map map, Player player, _, Creature far) = Fixture();

        Assert.False(Wanders(map, far), "the far creature should start out asleep");

        map.Relocate(player, new Position(FarAway - 5f, 0f, 0f, 0f));

        Assert.True(Wanders(map, far), "walking over to it should wake it up");
    }

    /// <summary>
    /// Ticks the map a few times and reports whether the creature went anywhere.
    /// </summary>
    /// <remarks>
    /// More than one tick because a wander is two steps: one update picks a destination and starts
    /// the move, and later updates advance along it. A single tick would report "did not move" for a
    /// creature that is being updated perfectly well, which is the false negative that would make
    /// this whole file lie.
    /// </remarks>
    private static bool Wanders(Map map, Creature creature)
    {
        Position before = creature.Position;

        for (int tick = 0; tick < 5; tick++)
        {
            map.Update(gameplayDiff: LongEnoughToWander, sessionDiff: 0);

            if (creature.Position != before)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A map with a player and two wandering creatures: one beside it, one far away.
    /// </summary>
    /// <remarks>
    /// Both creatures come in through the grid loader on the player's own grid, so both are filed
    /// into cells — the far one lands in a cell nothing makes active, which is the state under test.
    /// Loading it from its own grid instead would never load it at all, and the test would pass for
    /// the wrong reason.
    /// </remarks>
    private static (Map Map, Player Player, Creature Near, Creature Far) Fixture()
    {
        Creature near = CreatureFixture.Build(
            wanderDistance: 10f,
            movementType: 1,
            position: new Position(5f, 0f, 0f, 0f));

        Creature far = CreatureFixture.Build(
            wanderDistance: 10f,
            movementType: 1,
            position: new Position(FarAway, 0f, 0f, 0f));

        Map map = new(0, new TerrainMap(0, Path.GetTempPath()), new TwoCreatures(near, far));

        CharacterSummary summary = new(1, "Walker", 1, 1, 0, 0, 0, 0, 0, 0, 1, 12, 0, 0f, 0f, 0f, 0, 0, 0);
        ChrRacesEntry race = new(1, 0, 1, 49, 50, 7, 0, 0, "Human", 0);
        ChrClassesEntry characterClass = new(1, 1, "Warrior", 4, 0);
        PlayerBaseStats stats = new(20, 0, 23, 20, 22, 20, 20);

        Player player = Player.Create(summary, race, characterClass, stats);
        player.Position = new Position(0f, 0f, 0f, 0f);
        player.MaxHealth = 1000;
        player.Health = 1000;

        map.Add(player);

        GameRandom.SeedCurrentThread(20260806);

        return (map, player, near, far);
    }

    /// <summary>Hands both creatures to whichever grid the player arrives in.</summary>
    private sealed class TwoCreatures(Creature near, Creature far) : IGridObjectLoader
    {
        private bool _loaded;

        public IReadOnlyList<WorldObject> Load(uint mapId, GridCoord grid)
        {
            if (_loaded)
            {
                return [];
            }

            _loaded = true;
            return [near, far];
        }
    }
}
