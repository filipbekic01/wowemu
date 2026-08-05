using WowEmu.Core;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;

// The test namespace ends in `Unit`, which shadows the class of the same name. Verified required:
// removing this alias is a compile error, not a style nit, however the IDE greys it out.
using GameUnit = WowEmu.Game.Unit;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The threat list: who a creature hates, and which of them it actually fights.
/// </summary>
public sealed class ThreatManagerTests
{
    private static Creature At(float x, float y = 0f)
    {
        Creature creature = CreatureFixture.Build();
        creature.Position = new Position(x, y, 0f, 0f);

        return creature;
    }

    [Fact]
    public void ANewList_IsEmpty()
    {
        Creature owner = At(0f);

        Assert.True(owner.Threat.IsEmpty);
        Assert.Null(owner.Threat.SelectVictim());
    }

    [Fact]
    public void Threat_Accumulates()
    {
        Creature owner = At(0f);
        Creature attacker = At(2f);

        owner.Threat.AddThreat(attacker, 10f);
        owner.Threat.AddThreat(attacker, 15f);

        Assert.Equal(25f, owner.Threat.GetThreat(attacker));
        Assert.Equal(1, owner.Threat.Count);
    }

    /// <summary>
    /// Zero threat still puts an attacker on the list.
    /// </summary>
    /// <remarks>
    /// Not a no-op: it is how something that did no damage — a miss, a fully dodged swing — still
    /// gets a creature to fight back. Skipping zero would make an unlucky opening swing look like it
    /// never happened.
    /// </remarks>
    [Fact]
    public void ZeroThreat_StillJoinsTheList()
    {
        Creature owner = At(0f);
        Creature attacker = At(2f);

        owner.Threat.AddThreat(attacker, 0f);

        Assert.True(owner.Threat.Contains(attacker));
        Assert.Same(attacker, owner.Threat.SelectVictim());
    }

    /// <summary>Threat never goes negative.</summary>
    [Fact]
    public void Threat_NeverGoesNegative()
    {
        Creature owner = At(0f);
        Creature attacker = At(2f);

        owner.Threat.AddThreat(attacker, 10f);
        owner.Threat.AddThreat(attacker, -100f);

        Assert.Equal(0f, owner.Threat.GetThreat(attacker));
        Assert.True(owner.Threat.Contains(attacker));
    }

    [Fact]
    public void AUnit_DoesNotHateItself()
    {
        Creature owner = At(0f);

        owner.Threat.AddThreat(owner, 100f);

        Assert.True(owner.Threat.IsEmpty);
    }

    [Fact]
    public void TheList_IsSortedHighestFirst()
    {
        Creature owner = At(0f);
        Creature low = At(2f);
        Creature high = At(3f);
        Creature middle = At(4f);

        owner.Threat.AddThreat(low, 10f);
        owner.Threat.AddThreat(high, 100f);
        owner.Threat.AddThreat(middle, 50f);

        Assert.Equal(
            [high, middle, low],
            owner.Threat.Sorted.Select(entry => entry.Target));
    }

    // ------------------------------------------------------------------ the sticky victim

    /// <summary>With nobody on the slot, the highest threat takes it.</summary>
    [Fact]
    public void TheFirstVictim_IsSimplyTheHighest()
    {
        Creature owner = At(0f);
        Creature low = At(2f);
        Creature high = At(3f);

        owner.Threat.AddThreat(low, 10f);
        owner.Threat.AddThreat(high, 20f);

        Assert.Same(high, owner.Threat.SelectVictim());
    }

    /// <summary>
    /// Merely being ahead is not enough to take the victim slot.
    /// </summary>
    /// <remarks>
    /// This is the rule that makes tanking possible. Without it a creature switches the instant
    /// anyone edges ahead, so it spins between two similar attackers hitting neither — and holding
    /// aggro would mean winning every single comparison rather than staying 10 % clear.
    /// </remarks>
    [Fact]
    public void BeingSlightlyAhead_DoesNotStealTheVictimSlot()
    {
        Creature owner = At(0f);
        Creature tank = At(2f);
        Creature challenger = At(3f);

        owner.Threat.AddThreat(tank, 100f);
        Assert.Same(tank, owner.Threat.SelectVictim());

        // Ahead, but under the 10 % melee margin.
        owner.Threat.AddThreat(challenger, 105f);

        Assert.Equal(105f, owner.Threat.GetThreat(challenger));
        Assert.Same(tank, owner.Threat.SelectVictim());
    }

    /// <summary>Ten percent clear, in melee range, takes it.</summary>
    [Fact]
    public void TenPercentAheadInMeleeRange_TakesTheVictimSlot()
    {
        Creature owner = At(0f);
        Creature tank = At(2f);
        Creature challenger = At(3f);

        owner.Threat.AddThreat(tank, 100f);
        owner.Threat.SelectVictim();

        owner.Threat.AddThreat(challenger, 111f);

        Assert.Same(challenger, owner.Threat.SelectVictim());
    }

    /// <summary>
    /// Out of melee range the bar is 30 %, not 10 %.
    /// </summary>
    /// <remarks>
    /// Pulling a creature off what it is standing next to costs more than taking it from beside you.
    /// Applying the melee margin everywhere would let a ranged attacker rip aggro far too easily.
    /// </remarks>
    [Fact]
    public void OutOfMeleeRange_TheBarIsThirtyPercent()
    {
        Creature owner = At(0f);
        Creature tank = At(2f);
        Creature distant = At(60f);

        owner.Threat.AddThreat(tank, 100f);
        owner.Threat.SelectVictim();

        // Past the melee margin, short of the ranged one.
        owner.Threat.AddThreat(distant, 120f);
        Assert.Same(tank, owner.Threat.SelectVictim());

        // Past the ranged one.
        owner.Threat.AddThreat(distant, 20f);
        Assert.Equal(140f, owner.Threat.GetThreat(distant));
        Assert.Same(distant, owner.Threat.SelectVictim());
    }

    /// <summary>Losing the current victim frees the slot for the next highest outright.</summary>
    [Fact]
    public void WhenTheVictimDies_TheNextHighestTakesOverWithNoMargin()
    {
        Creature owner = At(0f);
        Creature tank = At(2f);
        Creature other = At(3f);

        owner.Threat.AddThreat(tank, 100f);
        owner.Threat.AddThreat(other, 50f);

        Assert.Same(tank, owner.Threat.SelectVictim());

        tank.DeathState = DeathState.Corpse;

        // No margin needed: there is no incumbent to beat.
        Assert.Same(other, owner.Threat.SelectVictim());
        Assert.False(owner.Threat.Contains(tank));
    }

    [Fact]
    public void SomethingOnAnotherMap_IsDroppedFromTheList()
    {
        Creature owner = At(0f);
        Creature gone = At(2f);

        owner.Threat.AddThreat(gone, 100f);
        gone.MapId = 571;

        Assert.Null(owner.Threat.SelectVictim());
        Assert.False(owner.Threat.Contains(gone));
    }

    /// <summary>
    /// A tie at the top is broken by distance.
    /// </summary>
    /// <remarks>
    /// Two attackers who have dealt identical damage will not compare exactly equal in floats, so
    /// the comparison is against a tolerance rather than for equality.
    /// </remarks>
    [Fact]
    public void ATieAtTheTop_IsBrokenByDistance()
    {
        Creature owner = At(0f);
        Creature far = At(40f);
        Creature near = At(3f);

        owner.Threat.AddThreat(far, 100f);
        owner.Threat.AddThreat(near, 100f);

        Assert.Same(near, owner.Threat.SelectVictim());
    }

    [Fact]
    public void Removing_ClearsTheVictimSlotToo()
    {
        Creature owner = At(0f);
        Creature attacker = At(2f);

        owner.Threat.AddThreat(attacker, 100f);
        Assert.Same(attacker, owner.Threat.SelectVictim());

        owner.Threat.Remove(attacker);

        Assert.Null(owner.Threat.CurrentVictim);
        Assert.Null(owner.Threat.SelectVictim());
    }

    [Fact]
    public void Clearing_ForgetsEveryone()
    {
        Creature owner = At(0f);

        owner.Threat.AddThreat(At(2f), 100f);
        owner.Threat.AddThreat(At(3f), 50f);
        owner.Threat.SelectVictim();

        owner.Threat.Clear();

        Assert.True(owner.Threat.IsEmpty);
        Assert.Null(owner.Threat.CurrentVictim);
    }

    /// <summary>
    /// A creature that keeps being hit does not thrash between similar attackers.
    /// </summary>
    /// <remarks>
    /// The behaviour the margins exist for, over a simulated fight rather than a single comparison:
    /// two attackers alternating damage should not produce a switch on every swing.
    /// </remarks>
    [Fact]
    public void TwoSimilarAttackers_DoNotMakeItThrash()
    {
        Creature owner = At(0f);
        Creature first = At(2f);
        Creature second = At(3f);

        int switches = 0;
        GameUnit? last = null;

        for (int swing = 0; swing < 100; swing++)
        {
            // Alternating, so the lead changes hands constantly.
            owner.Threat.AddThreat(swing % 2 == 0 ? first : second, 10f);

            GameUnit? victim = owner.Threat.SelectVictim();

            if (!ReferenceEquals(victim, last))
            {
                switches++;
                last = victim;
            }
        }

        // One switch to pick the first victim, and it holds — the alternating lead never reaches
        // 10 % clear. Without the margin this would be close to a switch per swing.
        Assert.Equal(1, switches);
    }
}

/// <summary>Threat generated by real swings, through a map.</summary>
public sealed class MapThreatTests
{
    /// <summary>Hitting something puts you on its list, with threat equal to the damage.</summary>
    [Fact]
    public void Damage_GeneratesThreatOnTheVictim()
    {
        (Map map, Player attacker, Creature victim, _) = MapCombatFixture.Engaged();

        map.Update(gameplayDiff: 100, sessionDiff: 100);

        Assert.True(victim.Threat.Contains(attacker));
        Assert.True(victim.Threat.GetThreat(attacker) > 0f, "a landed swing generated no threat");
    }

    /// <summary>Dying forgets everything, so a respawn does not come back angry.</summary>
    [Fact]
    public void Dying_ClearsTheThreatList()
    {
        (Map map, Player attacker, Creature victim, _) = MapCombatFixture.Engaged();

        victim.Health = 1;

        while (victim.IsAlive)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.True(victim.Threat.IsEmpty);
        Assert.False(victim.Threat.Contains(attacker));
    }
}
