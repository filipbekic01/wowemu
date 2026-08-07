using WowEmu.Game;
using WowEmu.Game.Maps;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// How a creature holds itself, and whether it ambles or runs.
/// </summary>
/// <remarks>
/// Both are drawn by the client from state the server sends, and neither was being sent after
/// spawn — so a creature that spawned sitting stayed seated through the fight it was having, and
/// every patrol played a run cycle at walking pace.
/// </remarks>
public sealed class StandStateTests
{
    /// <summary>A sitting creature gets up to fight.</summary>
    /// <remarks>
    /// <c>Creature::AtEngage</c>. Without it a sitting quest-giver swings from its chair.
    /// </remarks>
    [Fact]
    public void ASittingCreature_StandsUpToFight()
    {
        Creature creature = CreatureFixture.Build();
        creature.StandState = UnitStandState.Sit;

        Player target = InventoryFixture.Player();

        creature.Attack(target);

        Assert.Equal(UnitStandState.Stand, creature.StandState);
    }

    /// <summary>
    /// A kneeling one does not.
    /// </summary>
    /// <remarks>
    /// The odd one out, and deliberate — upstream leaves kneeling alone, so a creature scripted to
    /// kneel keeps doing so. Treating every non-standing pose as "get up" loses that.
    /// </remarks>
    [Fact]
    public void AKneelingCreature_StaysKneeling()
    {
        Creature creature = CreatureFixture.Build();
        creature.StandState = UnitStandState.Kneel;

        creature.Attack(InventoryFixture.Player());

        Assert.Equal(UnitStandState.Kneel, creature.StandState);
    }

    /// <summary>Every seated pose counts as sitting.</summary>
    [Theory]
    [InlineData(UnitStandState.Sit, true)]
    [InlineData(UnitStandState.SitChair, true)]
    [InlineData(UnitStandState.Sleep, true)]
    [InlineData(UnitStandState.SitHighChair, true)]
    [InlineData(UnitStandState.Submerged, true)]
    [InlineData(UnitStandState.Stand, false)]
    [InlineData(UnitStandState.Kneel, false)]
    [InlineData(UnitStandState.Dead, false)]
    public void SeatedPosesCountAsSitting(byte state, bool sitting) =>
        Assert.Equal(sitting, UnitStandState.IsSitting(state));

    // ------------------------------------------------------------------ walk and run

    /// <summary>Walking is a movement flag, separate from the speed.</summary>
    /// <remarks>
    /// The animation only. A creature told to run at walking speed plays one and moves at the other.
    /// </remarks>
    [Fact]
    public void Walking_IsAMovementFlag()
    {
        Creature creature = CreatureFixture.Build();

        Assert.False(creature.IsWalking);

        creature.IsWalking = true;

        Assert.True(creature.IsWalking);
        Assert.True(creature.Movement.Flags.HasFlag(MovementFlag.Walking));
    }

    /// <summary>
    /// Setting the mode reports whether it changed.
    /// </summary>
    /// <remarks>
    /// The packet goes to everyone watching, so a per-leg send would be one per waypoint for every
    /// patrolling creature on the continent. Upstream returns false for the same reason.
    /// </remarks>
    [Fact]
    public void SettingTheMode_ReportsWhetherItChanged()
    {
        Creature creature = CreatureFixture.Build();

        Assert.True(creature.SetWalk(true));
        Assert.False(creature.SetWalk(true));
        Assert.True(creature.SetWalk(false));
    }

    /// <summary>
    /// The map tells watchers when a creature changes mode, and only then.
    /// </summary>
    /// <remarks>
    /// The client picks the animation from this packet, not from the speed in the move — so without
    /// it a patrol sprints along on a run cycle at walking pace, which reads as a broken animation
    /// rather than a missing flag.
    /// <para>
    /// Note what does <b>not</b> produce a packet: a creature that was already running and starts a
    /// chase. Running is the default, so nothing changed and nothing is said. Sending on every move
    /// instead would be a packet per waypoint for every creature on the continent.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheMap_TellsWatchersWhenTheModeChanges()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) =
            MapCombatFixture.Engaged(distance: 30f);

        // Angry and out of reach, so it chases — and a chase runs.
        victim.Threat.AddThreat(player, 1f);

        // Ambling to begin with, so the chase is a real transition.
        victim.IsWalking = true;
        link.SplineModes.Clear();

        map.Update(gameplayDiff: 100, sessionDiff: 100);

        Assert.Contains(
            link.SplineModes,
            mode => mode.Unit == victim.Guid && mode.Opcode == Opcode.SMSG_SPLINE_MOVE_SET_RUN_MODE);

        int afterFirst = link.SplineModes.Count;

        // Still chasing, still running — nothing more to say.
        for (int i = 0; i < 5; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Equal(afterFirst, link.SplineModes.Count);
    }

    /// <summary>
    /// A creature that was already running says nothing when it starts a chase.
    /// </summary>
    /// <remarks>
    /// The common case by far, and the reason the change check exists rather than sending on every
    /// move.
    /// </remarks>
    [Fact]
    public void AlreadyRunning_SaysNothing()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) =
            MapCombatFixture.Engaged(distance: 30f);

        victim.Threat.AddThreat(player, 1f);

        Assert.False(victim.IsWalking);
        link.SplineModes.Clear();

        map.Update(gameplayDiff: 100, sessionDiff: 100);

        Assert.Empty(link.SplineModes);
    }

    /// <summary>Setting it back and forth flips the flag both ways.</summary>
    [Fact]
    public void TheFlag_ClearsAgain()
    {
        Creature creature = CreatureFixture.Build();

        creature.SetWalk(true);
        creature.SetWalk(false);

        Assert.False(creature.IsWalking);
        Assert.False(creature.Movement.Flags.HasFlag(MovementFlag.Walking));
    }
}
