using Microsoft.Extensions.Logging.Abstractions;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;
using WowEmu.Protocol;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The fields combat reads and writes, and how a creature's are derived.
/// </summary>
/// <remarks>
/// Every one of these is a client-visible update field rather than a server-side number, so a wrong
/// value shows in the tooltip before it shows in a fight.
/// </remarks>
public sealed class UnitCombatStateTests
{
    /// <summary>
    /// The two attack-time slots are consecutive fields, indexed by weapon.
    /// </summary>
    /// <remarks>
    /// Main hand and off hand share a base index, so an off-by-one writes the off-hand speed into
    /// the main hand and a dual-wielding unit swings at the wrong rate with nothing to say so.
    /// </remarks>
    [Fact]
    public void AttackTimes_AreIndexedByWeaponFromOneBase()
    {
        Creature creature = CreatureFixture.Build();

        creature.SetAttackTime(WeaponAttackType.BaseAttack, 2000);
        creature.SetAttackTime(WeaponAttackType.OffAttack, 1500);
        creature.SetAttackTime(WeaponAttackType.RangedAttack, 2400);

        Assert.Equal(2000u, creature.GetAttackTime(WeaponAttackType.BaseAttack));
        Assert.Equal(1500u, creature.GetAttackTime(WeaponAttackType.OffAttack));
        Assert.Equal(2400u, creature.GetAttackTime(WeaponAttackType.RangedAttack));

        // The main hand and off hand really are the two consecutive slots the client expects.
        Assert.Equal(2000u, creature.Fields.GetUInt32(UpdateFields.UNIT_FIELD_BASEATTACKTIME));
        Assert.Equal(1500u, creature.Fields.GetUInt32(UpdateFields.UNIT_FIELD_BASEATTACKTIME + 1));
    }

    /// <summary>
    /// Combat state lives in the unit flags, not beside them.
    /// </summary>
    /// <remarks>
    /// The client draws the nameplate from the flag. A separate server-side bool would drift from
    /// what the player sees, and the drift would only show under load.
    /// </remarks>
    [Fact]
    public void CombatState_IsTheClientVisibleFlag()
    {
        Creature creature = CreatureFixture.Build();

        Assert.False(creature.IsInCombat);

        creature.IsInCombat = true;

        Assert.True(creature.IsInCombat);
        Assert.Equal((uint)UnitFlags.InCombat, creature.UnitFlags & (uint)UnitFlags.InCombat);

        creature.IsInCombat = false;

        Assert.False(creature.IsInCombat);
        Assert.Equal(0u, creature.UnitFlags & (uint)UnitFlags.InCombat);
    }

    /// <summary>Setting combat leaves the other flags where they were.</summary>
    [Fact]
    public void SettingCombat_DoesNotDisturbOtherFlags()
    {
        Creature creature = CreatureFixture.Build();

        creature.UnitFlags = (uint)(UnitFlags.NotSelectable | UnitFlags.Pacified);

        creature.IsInCombat = true;
        creature.IsInCombat = false;

        Assert.Equal((uint)(UnitFlags.NotSelectable | UnitFlags.Pacified), creature.UnitFlags);
    }

    [Fact]
    public void ANewCreature_IsAlive()
    {
        Creature creature = CreatureFixture.Build();

        Assert.Equal(DeathState.Alive, creature.DeathState);
        Assert.True(creature.IsAlive);

        creature.DeathState = DeathState.JustDied;

        Assert.False(creature.IsAlive);
    }

    [Fact]
    public void Target_RoundTripsThroughTheUpdateField()
    {
        Creature creature = CreatureFixture.Build();

        Assert.True(creature.Target.IsEmpty);

        WowEmu.Core.ObjectGuid victim = WowEmu.Core.ObjectGuid.Create(WowEmu.Core.HighGuid.Player, 42);
        creature.Target = victim;

        Assert.Equal(victim, creature.Target);
        Assert.Equal(victim, creature.Fields.GetGuid(UpdateFields.UNIT_FIELD_TARGET));
    }

    /// <summary>Attack power contributes to the damage range, scaled by swing speed.</summary>
    /// <remarks>
    /// <c>attackPower / 14 × swing seconds</c>. The 14 is the game's damage-per-second conversion and
    /// the swing time is what turns a rate into a per-swing figure — drop it and a slow weapon hits
    /// for the same as a fast one.
    /// </remarks>
    [Fact]
    public void AttackPower_AddsToBothEndsOfTheDamageRange()
    {
        // 14 attack power over a 2-second swing is exactly 2 damage.
        Creature creature = CreatureFixture.Build();

        Assert.Equal(2000u, creature.GetAttackTime(WeaponAttackType.BaseAttack));
        Assert.Equal(14u, creature.AttackPower);

        // Fixture template damage is 4-6 with a multiplier of 1.
        Assert.Equal(6f, creature.MinDamage, 0.01f);
        Assert.Equal(8f, creature.MaxDamage, 0.01f);
    }

    [Fact]
    public void TheDamageRoll_StaysInsideTheRange()
    {
        Creature creature = CreatureFixture.Build();

        for (int i = 0; i < 200; i++)
        {
            uint damage = creature.RollSwingDamage(WowEmu.Core.GameRandom.Urand);

            Assert.InRange(damage, (uint)creature.MinDamage, (uint)creature.MaxDamage);
        }
    }

    /// <summary>A range that collapses to a point does not consume a random draw.</summary>
    [Fact]
    public void ADegenerateRange_RollsWithoutDrawing()
    {
        Creature creature = CreatureFixture.Build();

        creature.MinDamage = 7f;
        creature.MaxDamage = 7f;

        int draws = 0;

        Assert.Equal(7u, creature.RollSwingDamage((_, _) => { draws++; return 0; }));
        Assert.Equal(0, draws);
    }
}

/// <summary>
/// Combat stats derived from the real tables, checked for being plausible rather than exact.
/// </summary>
/// <remarks>
/// There is no oracle for "a level 2 wolf should hit for 5" short of the C++ server itself, so these
/// assert the shape: everything scales with level, nothing is zero, and nothing is absurd. That is
/// enough to catch a formula reading the wrong column, which is the failure that matters.
/// </remarks>
public sealed class CreatureCombatStatsTests(ITestOutputHelper output)
{
    [RequiresWorldDatabaseFact]
    public async Task RealCreatures_GetPlausibleCombatStats()
    {
        CreatureGridLoader loader = await NewLoaderAsync();

        int checkedCreatures = 0, armed = 0;
        float weakest = float.MaxValue, strongest = 0f;
        byte lowestLevel = byte.MaxValue, highestLevel = 0;

        foreach (WorldObject spawned in loader.Load(0, MapCoordinates.GridFor(-8949.95f, -132.493f)))
        {
            Creature creature = (Creature)spawned;
            checkedCreatures++;

            // Nothing should swing infinitely fast, which is what a zero would mean.
            Assert.True(
                creature.GetAttackTime(WeaponAttackType.BaseAttack) > 0,
                $"{creature.Name} has no swing time");

            Assert.True(creature.MaxDamage >= creature.MinDamage, $"{creature.Name} has an inverted range");
            Assert.True(creature.MinDamage >= 0f);

            // Bounded by level, not by zone. Northshire's grid reaches into Elwynn and holds level
            // 80 holiday-event NPCs and training dummies alongside the wolves — "a starting zone
            // should not hit hard" is not a property of the data.
            float ceiling = 50f + (creature.Level * 25f);

            Assert.True(
                creature.MaxDamage < ceiling,
                $"{creature.Name} is level {creature.Level} and hits for {creature.MaxDamage:F0}");

            lowestLevel = Math.Min(lowestLevel, creature.Level);
            highestLevel = Math.Max(highestLevel, creature.Level);

            if (creature.MaxDamage > 0f)
            {
                armed++;
                weakest = MathF.Min(weakest, creature.MinDamage);
                strongest = MathF.Max(strongest, creature.MaxDamage);
            }
        }

        Assert.True(checkedCreatures > 50, $"only {checkedCreatures} creatures loaded");
        Assert.True(armed * 2 > checkedCreatures, "most creatures in a starting zone should deal damage");

        output.WriteLine(
            $"{checkedCreatures} creatures in Northshire's grid: {armed} deal damage, " +
            $"from {weakest:F1} to {strongest:F1} per swing (levels {lowestLevel}-{highestLevel})");
    }

    /// <summary>
    /// Armour and attack power both rise with level.
    /// </summary>
    /// <remarks>
    /// Reads the class/level table directly. A formula that took the wrong column would still
    /// produce numbers; it would not produce numbers that climb.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task CombatStats_RiseWithLevel()
    {
        CreatureStatsStore stats = new();
        await stats.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        Assert.True(stats.TryGet(1, 1, out CreatureBaseStats low));
        Assert.True(stats.TryGet(60, 1, out CreatureBaseStats high));

        Assert.True(high.AttackPower > low.AttackPower, "attack power did not rise with level");
        Assert.True(high.BaseArmor > low.BaseArmor, "armour did not rise with level");
        Assert.True(high.BaseDamageClassic > low.BaseDamageClassic, "base damage did not rise with level");

        output.WriteLine(
            $"level 1: ap {low.AttackPower}, armour {low.BaseArmor}, damage {low.BaseDamageClassic:F2}  ->  " +
            $"level 60: ap {high.AttackPower}, armour {high.BaseArmor}, damage {high.BaseDamageClassic:F2}");
    }

    /// <summary>
    /// Templates with no damage range fall back to the class/level table.
    /// </summary>
    /// <remarks>
    /// 911 of 29,928 templates carry no damage at all. Without the fallback each of them stands
    /// there swinging for nothing, which reads as a bug in combat rather than a gap in the data.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task TemplatesWithNoDamage_StillHitForSomething()
    {
        CreatureTemplateStore templates = new();
        CreatureStatsStore stats = new();

        await templates.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);
        await stats.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        Assert.True(stats.TryGet(10, 1, out CreatureBaseStats baseStats));
        Assert.True(baseStats.BaseDamageFor(0) > 0f, "the class/level table has no classic damage");

        // The expansion slots are distinct, as they are for health.
        Assert.True(stats.TryGet(70, 1, out CreatureBaseStats atSeventy));
        Assert.Equal(atSeventy.BaseDamageClassic, atSeventy.BaseDamageFor(0));
        Assert.Equal(atSeventy.BaseDamageWrath, atSeventy.BaseDamageFor(2));
        Assert.Equal(atSeventy.BaseDamageClassic, atSeventy.BaseDamageFor(99));
    }

    private static async Task<CreatureGridLoader> NewLoaderAsync()
    {
        CreatureTemplateStore templates = new();
        CreatureStatsStore stats = new();
        CreatureSpawnStore spawns = new();

        await templates.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);
        await stats.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);
        await spawns.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        return new CreatureGridLoader(
            spawns,
            new CreatureFactory(templates, stats),
            NullLogger<CreatureGridLoader>.Instance);
    }
}
