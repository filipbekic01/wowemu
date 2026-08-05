using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Aggro range, which turns on the level difference and its two clamps.
/// </summary>
public sealed class AggroRangeTests
{
    private static Creature AtLevel(byte level)
    {
        Creature creature = CreatureFixture.Build();
        creature.Level = level;

        return creature;
    }

    [Fact]
    public void AgainstAnEqual_TheRadiusIsTwenty()
    {
        Creature creature = AtLevel(30);
        Creature target = AtLevel(30);

        Assert.Equal(CreatureAi.BaseAggroRadius, CreatureAi.AggroRadius(creature, target));
    }

    /// <summary>
    /// A creature notices something weaker from further away, and something stronger only up close.
    /// </summary>
    /// <remarks>
    /// The direction is the trap. Upstream's <c>GetAggroRange</c> has its two locals named the wrong
    /// way round, so reading it literally produces a radius that grows in the wrong direction —
    /// which is subtle enough to survive a play test, because a wrong-way radius still aggroes.
    /// </remarks>
    [Theory]
    [InlineData(30, 25, 25f)]   // target five levels below: five yards further
    [InlineData(30, 35, 15f)]   // target five levels above: five yards closer
    [InlineData(30, 30, 20f)]
    public void TheRadius_MovesWithTheLevelDifference(byte creatureLevel, byte targetLevel, float expected)
    {
        Creature creature = AtLevel(creatureLevel);
        Creature target = AtLevel(targetLevel);

        Assert.Equal(expected, CreatureAi.AggroRadius(creature, target));
    }

    /// <summary>
    /// A much stronger target never shrinks the radius below five yards.
    /// </summary>
    /// <remarks>
    /// The floor matters more than the cap. Without it the arithmetic goes negative against a
    /// high-level target and a low-level creature could never be pulled at all — a whole starting
    /// zone that ignores you.
    /// </remarks>
    [Fact]
    public void TheRadius_NeverFallsBelowTheFloor()
    {
        Creature creature = AtLevel(1);
        Creature target = AtLevel(80);

        Assert.Equal(CreatureAi.MinAggroRadius, CreatureAi.AggroRadius(creature, target));
    }

    /// <summary>Past 25 levels of advantage the radius stops growing.</summary>
    [Fact]
    public void TheRadius_IsCappedByTheLevelDifferenceAndByTheCeiling()
    {
        Creature creature = AtLevel(80);

        // 25 levels below: 20 + 25 = 45, exactly the ceiling.
        Assert.Equal(45f, CreatureAi.AggroRadius(creature, AtLevel(55)));

        // Further below still: the difference is clamped, so the radius does not keep growing.
        Assert.Equal(45f, CreatureAi.AggroRadius(creature, AtLevel(1)));
        Assert.Equal(CreatureAi.MaxAggroRadius, CreatureAi.AggroRadius(creature, AtLevel(1)));
    }
}

/// <summary>Whether a creature picks a fight at all.</summary>
public sealed class CanStartAttackTests
{
    private static (Creature Creature, Creature Target) Pair(float distance, float heightDifference = 0f)
    {
        Creature creature = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        creature.Position = new Position(0f, 0f, 0f, 0f);
        target.Position = new Position(distance, 0f, heightDifference, 0f);

        return (creature, target);
    }

    private static bool CanSee() => true;

    private static bool Blind() => false;

    [Fact]
    public void SomethingHostileInRange_IsAttacked()
    {
        (Creature creature, Creature target) = Pair(distance: 10f);

        Assert.True(CreatureAi.CanStartAttack(creature, target, isHostile: true, CanSee));
    }

    [Fact]
    public void SomethingFriendly_IsLeftAlone()
    {
        (Creature creature, Creature target) = Pair(distance: 10f);

        Assert.False(CreatureAi.CanStartAttack(creature, target, isHostile: false, CanSee));
    }

    [Fact]
    public void SomethingOutOfRange_IsLeftAlone()
    {
        (Creature creature, Creature target) = Pair(distance: 100f);

        Assert.False(CreatureAi.CanStartAttack(creature, target, isHostile: true, CanSee));
    }

    /// <summary>
    /// Something far above or below is left alone, whatever the flat distance.
    /// </summary>
    /// <remarks>
    /// Cheap arithmetic standing in for what would otherwise be a ray cast per creature per player
    /// per tick — and it is what stops a creature on the ground aggroing someone on a bridge
    /// directly overhead.
    /// </remarks>
    [Fact]
    public void SomethingWellAboveOrBelow_IsLeftAlone()
    {
        (Creature creature, Creature above) = Pair(distance: 5f, heightDifference: 10f);
        Assert.False(CreatureAi.CanStartAttack(creature, above, isHostile: true, CanSee));

        (Creature other, Creature below) = Pair(distance: 5f, heightDifference: -10f);
        Assert.False(CreatureAi.CanStartAttack(other, below, isHostile: true, CanSee));
    }

    [Fact]
    public void SomethingBehindAWall_IsLeftAlone()
    {
        (Creature creature, Creature target) = Pair(distance: 10f);

        Assert.False(CreatureAi.CanStartAttack(creature, target, isHostile: true, Blind));
    }

    /// <summary>
    /// Line of sight is only consulted once everything cheaper has passed.
    /// </summary>
    /// <remarks>
    /// A ray cast per creature per nearby player per tick would dominate the tick. The ordering is
    /// the optimisation, so it is worth pinning rather than leaving to be re-derived.
    /// </remarks>
    [Fact]
    public void LineOfSight_IsTheLastThingChecked()
    {
        (Creature creature, Creature target) = Pair(distance: 500f);

        int rayCasts = 0;

        CreatureAi.CanStartAttack(creature, target, isHostile: true, () => { rayCasts++; return true; });

        Assert.Equal(0, rayCasts);
    }

    [Fact]
    public void APassiveCreature_NeverStartsAnything()
    {
        (Creature creature, Creature target) = Pair(distance: 5f);

        creature.React = ReactState.Passive;
        Assert.False(CreatureAi.CanStartAttack(creature, target, isHostile: true, CanSee));

        creature.React = ReactState.Defensive;
        Assert.False(CreatureAi.CanStartAttack(creature, target, isHostile: true, CanSee));
    }

    /// <summary>Something already fighting does not go looking for someone else.</summary>
    [Fact]
    public void ACreatureAlreadyFighting_DoesNotPickUpAnother()
    {
        (Creature creature, Creature target) = Pair(distance: 5f);

        creature.Attack(CreatureFixture.Build());

        Assert.False(CreatureAi.CanStartAttack(creature, target, isHostile: true, CanSee));
    }

    [Fact]
    public void TheDead_NeitherAggroNorAreAggroed()
    {
        (Creature creature, Creature target) = Pair(distance: 5f);

        target.DeathState = DeathState.Corpse;
        Assert.False(CreatureAi.CanStartAttack(creature, target, isHostile: true, CanSee));

        target.DeathState = DeathState.Alive;
        creature.DeathState = DeathState.Corpse;
        Assert.False(CreatureAi.CanStartAttack(creature, target, isHostile: true, CanSee));
    }
}

/// <summary>Chasing, and giving up.</summary>
public sealed class CreatureAiUpdateTests
{
    private static (Creature Creature, Creature Enemy) Engaged(float distance)
    {
        Creature creature = CreatureFixture.Build();
        Creature enemy = CreatureFixture.Build();

        creature.Position = creature.HomePosition;
        enemy.Position = new Position(
            creature.HomePosition.X + distance, creature.HomePosition.Y, creature.HomePosition.Z, 0f);

        creature.Threat.AddThreat(enemy, 100f);

        return (creature, enemy);
    }

    [Fact]
    public void SomethingOnTheThreatList_BecomesTheVictim()
    {
        (Creature creature, Creature enemy) = Engaged(distance: 2f);

        AiDecision decision = CreatureAi.Update(creature);

        Assert.Same(enemy, decision.Victim);
        Assert.Same(enemy, creature.Victim);
        Assert.True(creature.IsInCombat);
    }

    /// <summary>Something in reach is not chased.</summary>
    /// <remarks>
    /// A move issued every tick towards something already adjacent has the creature shuffling into
    /// its target, which reads as jitter and costs a packet per tick per fight.
    /// </remarks>
    [Fact]
    public void SomethingInReach_IsNotChased()
    {
        (Creature creature, _) = Engaged(distance: 2f);

        Assert.Null(CreatureAi.Update(creature).Chase);
    }

    [Fact]
    public void SomethingOutOfReach_IsChased()
    {
        (Creature creature, Creature enemy) = Engaged(distance: 15f);

        AiDecision decision = CreatureAi.Update(creature);

        Assert.NotNull(decision.Chase);
        Assert.Equal(enemy.Position.X, decision.Chase!.Value.X, 0.001f);
    }

    /// <summary>
    /// Dragged past the leash radius, a creature gives up and heads home.
    /// </summary>
    /// <remarks>
    /// Measured from where it spawned, not from where the fight started. Measuring from the fight
    /// would let a creature be walked across a zone in short hops and never reset — which is exactly
    /// how a mob gets trained into a city.
    /// </remarks>
    [Fact]
    public void DraggedTooFarFromHome_ItGivesUp()
    {
        (Creature creature, _) = Engaged(distance: 2f);

        CreatureAi.Update(creature);
        Assert.NotNull(creature.Victim);

        // Walked well past the leash radius.
        creature.Position = new Position(
            creature.HomePosition.X + CreatureAi.LeashRadius + 10f,
            creature.HomePosition.Y,
            creature.HomePosition.Z,
            0f);

        AiDecision decision = CreatureAi.Update(creature);

        Assert.True(decision.Evaded);
        Assert.Null(decision.Victim);
        Assert.Null(creature.Victim);
        Assert.False(creature.IsInCombat);
    }

    /// <summary>
    /// Evading forgets the threat list, not only the victim.
    /// </summary>
    /// <remarks>
    /// Keeping it would have the creature re-acquire the same target the instant it got home and
    /// walk straight back out — an evade that evades nothing.
    /// </remarks>
    [Fact]
    public void Evading_ForgetsTheThreatList()
    {
        (Creature creature, Creature enemy) = Engaged(distance: 2f);

        CreatureAi.Update(creature);
        CreatureAi.Evade(creature);

        Assert.True(creature.Threat.IsEmpty);
        Assert.False(creature.Threat.Contains(enemy));
    }

    /// <summary>Just inside the leash radius it keeps fighting.</summary>
    [Fact]
    public void JustInsideTheLeashRadius_ItKeepsFighting()
    {
        (Creature creature, _) = Engaged(distance: 2f);

        CreatureAi.Update(creature);

        creature.Position = new Position(
            creature.HomePosition.X + CreatureAi.LeashRadius - 1f,
            creature.HomePosition.Y,
            creature.HomePosition.Z,
            0f);

        Assert.False(CreatureAi.Update(creature).Evaded);
        Assert.NotNull(creature.Victim);
    }

    /// <summary>When the threat list empties, the creature heads home rather than standing there.</summary>
    [Fact]
    public void WhenNothingIsLeftToFight_ItHeadsHome()
    {
        (Creature creature, Creature enemy) = Engaged(distance: 2f);

        CreatureAi.Update(creature);

        enemy.DeathState = DeathState.Corpse;

        AiDecision decision = CreatureAi.Update(creature);

        Assert.True(decision.Evaded);
        Assert.Equal(creature.HomePosition, decision.Chase);
    }

    /// <summary>
    /// A creature in reach turns to face its victim.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. The swing loop refuses to attack anything outside a 120° cone, so a creature
    /// that never turns stands next to its victim retrying every 100 ms and never lands a hit —
    /// which looks exactly like a damage bug rather than a facing one.
    /// </remarks>
    [Fact]
    public void ACreatureInReach_TurnsToFaceItsVictim()
    {
        Creature creature = CreatureFixture.Build();
        Creature enemy = CreatureFixture.Build();

        // Enemy directly behind: the creature faces +x, the enemy is at -x.
        creature.Position = creature.HomePosition with { Orientation = 0f };
        enemy.Position = creature.HomePosition with { X = creature.HomePosition.X - 3f };

        creature.Threat.AddThreat(enemy, 100f);

        Assert.False(MeleeSwing.IsFacing(creature, enemy), "the fixture should start facing away");

        CreatureAi.Update(creature);

        Assert.True(MeleeSwing.IsFacing(creature, enemy), "the creature did not turn to face its victim");
        Assert.Equal(MathF.PI, MathF.Abs(creature.Position.Orientation), 0.001f);
    }

    /// <summary>Something being chased is not turned towards — the move already points that way.</summary>
    [Fact]
    public void ACreatureChasing_IsLeftForTheMoveToTurn()
    {
        Creature creature = CreatureFixture.Build();
        Creature enemy = CreatureFixture.Build();

        creature.Position = creature.HomePosition with { Orientation = 0f };
        enemy.Position = creature.HomePosition with { X = creature.HomePosition.X - 20f };

        creature.Threat.AddThreat(enemy, 100f);

        AiDecision decision = CreatureAi.Update(creature);

        Assert.NotNull(decision.Chase);
        Assert.Equal(0f, creature.Position.Orientation);
    }

    [Fact]
    public void ACreatureWithNothingToFight_DoesNothing()
    {
        Creature creature = CreatureFixture.Build();

        AiDecision decision = CreatureAi.Update(creature);

        Assert.Null(decision.Victim);
        Assert.Null(decision.Chase);
        Assert.False(decision.Evaded);
    }
}

/// <summary>Creature AI driven through a real map, which is where it fights back.</summary>
public sealed class MapCreatureAiTests
{
    /// <summary>
    /// A creature that is hit hits back.
    /// </summary>
    /// <remarks>
    /// The whole point of the threat list reaching the AI: the creature was never told to attack,
    /// it worked it out from having been damaged.
    /// </remarks>
    [Fact]
    public void ACreatureThatIsHit_FightsBack()
    {
        (Map map, Player attacker, Creature victim, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        // Plenty of health, so the fight lasts long enough for it to answer.
        victim.MaxHealth = 100_000;
        victim.Health = 100_000;

        uint playerHealthBefore = attacker.Health;

        for (int i = 0; i < 60; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Same(attacker, victim.Victim);
        Assert.True(victim.IsInCombat);
        Assert.True(attacker.Health < playerHealthBefore, "the creature never hit back");

        // The player is told about swings in both directions.
        Assert.Contains(link.Swings, swing => swing.Attacker == victim.Guid);
    }

    /// <summary>A creature dragged past its leash gives up and heads home.</summary>
    [Fact]
    public void ACreatureDraggedTooFar_Evades()
    {
        (Map map, Player attacker, Creature victim, _) = MapCombatFixture.Engaged();

        victim.MaxHealth = 100_000;
        victim.Health = 100_000;

        // Get it angry.
        for (int i = 0; i < 20; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.NotNull(victim.Victim);

        // Drag it well past the leash radius from where it spawned.
        victim.Position = new Position(
            victim.HomePosition.X + CreatureAi.LeashRadius + 50f,
            victim.HomePosition.Y,
            victim.HomePosition.Z,
            0f);

        map.Update(gameplayDiff: 100, sessionDiff: 100);

        Assert.Null(victim.Victim);
        Assert.False(victim.IsInCombat);
        Assert.True(victim.Threat.IsEmpty);
    }

    /// <summary>
    /// With no faction data nothing starts a fight on its own.
    /// </summary>
    /// <remarks>
    /// The safe failure. A map without <c>FactionTemplate.dbc</c> has creatures that stand there,
    /// which is noticed — rather than creatures that attack everything, which reads as a rule.
    /// </remarks>
    [Fact]
    public void WithNoFactionData_NothingAggroesOnItsOwn()
    {
        (Map map, Player attacker, Creature victim, _) = MapCombatFixture.Engaged();

        // The fixture builds a map with no faction store, and the player has not been attacked.
        attacker.AttackStop();
        victim.Threat.Clear();

        for (int i = 0; i < 50; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Null(victim.Victim);
    }
}

/// <summary>
/// Faction hostility, read from the client's own <c>FactionTemplate.dbc</c>.
/// </summary>
public sealed class FactionTemplateTests(ITestOutputHelper output)
{
    private static FactionTemplateEntry Template(
        uint id, uint faction = 0, uint ourMask = 0, uint friendlyMask = 0, uint hostileMask = 0,
        uint[]? enemies = null, uint[]? friends = null) =>
        new(id, faction, 0, ourMask, friendlyMask, hostileMask,
            enemies ?? [0, 0, 0, 0], friends ?? [0, 0, 0, 0]);

    /// <summary>The broad masks decide when nothing more specific applies.</summary>
    [Fact]
    public void TheMasks_DecideWhenNothingSpecificApplies()
    {
        FactionTemplateEntry monster = Template(1, faction: 10, ourMask: 8, hostileMask: 1);
        FactionTemplateEntry player = Template(2, faction: 20, ourMask: 1);

        Assert.True(monster.IsHostileTo(player));
        Assert.False(player.IsHostileTo(monster));
    }

    /// <summary>
    /// A named enemy beats the masks, and is checked before the friend list.
    /// </summary>
    /// <remarks>
    /// This ordering is how a guard is hostile to one enemy city while ignoring neutral travellers.
    /// Consulting the masks first would erase every such exception, and consulting friends first
    /// would erase the ones that appear on both lists.
    /// </remarks>
    [Fact]
    public void ANamedEnemy_BeatsTheMasks()
    {
        FactionTemplateEntry guard = Template(1, faction: 10, ourMask: 2, friendlyMask: 2, enemies: [99, 0, 0, 0]);
        FactionTemplateEntry rival = Template(2, faction: 99, ourMask: 2);

        Assert.True(guard.IsHostileTo(rival));
        Assert.False(guard.IsFriendlyTo(rival));
    }

    /// <summary>A named friend beats a hostile mask.</summary>
    [Fact]
    public void ANamedFriend_BeatsTheMasks()
    {
        FactionTemplateEntry creature = Template(1, faction: 10, hostileMask: 2, friends: [50, 0, 0, 0]);
        FactionTemplateEntry ally = Template(2, faction: 50, ourMask: 2);

        Assert.False(creature.IsHostileTo(ally));
        Assert.True(creature.IsFriendlyTo(ally));
    }

    /// <summary>Sharing a faction is always friendly, whatever the masks say.</summary>
    [Fact]
    public void SharingAFaction_IsAlwaysFriendly()
    {
        FactionTemplateEntry first = Template(1, faction: 10, hostileMask: 255);
        FactionTemplateEntry second = Template(2, faction: 10, ourMask: 255);

        Assert.True(first.IsFriendlyTo(second));
    }

    /// <summary>Neutral to all: no enemies, no masks, no opinions.</summary>
    [Fact]
    public void ACritter_IsNeutralToAll()
    {
        Assert.True(Template(1, faction: 10).IsNeutralToAll);
        Assert.False(Template(2, faction: 10, hostileMask: 1).IsNeutralToAll);
        Assert.False(Template(3, faction: 10, enemies: [5, 0, 0, 0]).IsNeutralToAll);
    }

    /// <summary>
    /// Real factions from the client behave as the game does.
    /// </summary>
    /// <remarks>
    /// The unit tests above pin the rules against constructed templates; this pins that the file is
    /// being read with the right column layout. A single field out of place in the format string
    /// shifts everything after it and produces plausible-looking numbers.
    /// </remarks>
    [RequiresClientDataFact]
    public void RealFactions_BehaveAsTheGameDoes()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        DbcStore<FactionTemplateEntry> factions = stores.FactionTemplates;

        Assert.True(factions.Count > 0, "no faction templates loaded");

        // 14 and 16 are the two "Monster" templates: in the monster group, hostile to players. Between
        // them they cover 4,771 of the world's creature templates, so they are what a fight is with.
        Assert.True(factions.TryGet(14, out FactionTemplateEntry monster));
        Assert.True(factions.TryGet(16, out FactionTemplateEntry monsterSpar));

        Assert.True(monster.IsHostileToPlayers, "faction 14 is the ordinary hostile monster");
        Assert.True(monsterSpar.IsHostileToPlayers);

        // 35 is Friendly — the single most common creature faction, and it fights nobody. A vendor
        // reading as hostile would make every town unusable.
        Assert.True(factions.TryGet(35, out FactionTemplateEntry friendly));
        Assert.False(friendly.IsHostileToPlayers, "faction 35 is the friendly-to-everyone template");

        // 7 is Creature: no masks and no named enemies at all. Critters, and the reason a field of
        // rabbits does not mob anyone who walks past.
        Assert.True(factions.TryGet(7, out FactionTemplateEntry critter));
        Assert.True(critter.IsNeutralToAll, "faction 7 should pick no fights");

        // The two player factions: hostile to each other, never to themselves.
        Assert.True(factions.TryGet(1, out FactionTemplateEntry human));
        Assert.True(factions.TryGet(2, out FactionTemplateEntry orc));

        Assert.True(human.IsHostileTo(orc), "Alliance and Horde should be hostile");
        Assert.True(orc.IsHostileTo(human));
        Assert.False(human.IsHostileTo(human), "a faction is never hostile to itself");
        Assert.True(human.IsFriendlyTo(human));

        // And a monster attacks either of them.
        Assert.True(monster.IsHostileTo(human));
        Assert.True(monster.IsHostileTo(orc));

        output.WriteLine($"{factions.Count} faction templates loaded");

        foreach ((string label, FactionTemplateEntry entry) in ((string, FactionTemplateEntry)[])
                 [("14 monster", monster), ("16 monster", monsterSpar), ("35 friendly", friendly),
                  ("7 critter", critter), ("1 human", human), ("2 orc", orc)])
        {
            output.WriteLine(
                $"  {label,-12} our={entry.OurMask,3} friendly={entry.FriendlyMask,3} hostile={entry.HostileMask,3}");
        }
    }

    /// <summary>
    /// A faction the data does not describe is treated as harmless.
    /// </summary>
    /// <remarks>
    /// The safe direction to fail. A missing template that defaulted to hostile would produce a zone
    /// attacking on sight, which reads as a game rule rather than as missing data; one that defaults
    /// to harmless produces a creature that never fights, which is noticed immediately.
    /// </remarks>
    [Fact]
    public void AnUnknownFaction_IsTreatedAsHarmless()
    {
        DbcStore<FactionTemplateEntry> empty = DbcStore<FactionTemplateEntry>.Empty;

        Creature creature = CreatureFixture.Build();
        Creature other = CreatureFixture.Build();

        Assert.False(CreatureAi.IsHostile(empty, creature, other));
    }
}
