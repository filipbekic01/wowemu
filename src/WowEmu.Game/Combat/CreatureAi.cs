using WowEmu.Core;
using WowEmu.Data.Client;

namespace WowEmu.Game.Combat;

/// <summary>How a creature reacts to what walks past it. <c>ReactStates</c>.</summary>
public enum ReactState : byte
{
    /// <summary>Never fights back, whatever happens.</summary>
    Passive = 0,

    /// <summary>Fights back when hit, but never starts anything.</summary>
    Defensive = 1,

    /// <summary>Attacks anything hostile that comes close enough.</summary>
    Aggressive = 2,
}

/// <summary>What a tick of AI decided to do.</summary>
/// <param name="Victim">Who it is fighting, or null.</param>
/// <param name="Chase">Where it should walk to reach its victim, or null if it need not move.</param>
/// <param name="Evaded">Whether it gave up and headed home this tick.</param>
public readonly record struct AiDecision(Unit? Victim, Position? Chase, bool Evaded);

/// <summary>
/// Aggro, chase and evade — enough for a creature to fight back.
/// </summary>
/// <remarks>
/// Port of the parts of <c>CreatureAI</c>, <c>Creature::CanStartAttack</c> and
/// <c>Creature::GetAttackDistance</c> that a plain fight needs. No scripts, no spells, no
/// call-for-help.
/// </remarks>
public static class CreatureAi
{
    /// <summary>Aggro radius against something of the creature's own level.</summary>
    public const float BaseAggroRadius = 20.0f;

    /// <summary>Nothing aggroes from further than this, however big the level gap.</summary>
    public const float MaxAggroRadius = 45.0f;

    /// <summary>
    /// The closest anything aggroes from, which is roughly melee range.
    /// </summary>
    /// <remarks>
    /// The floor matters more than the ceiling: without it a low-level creature would have a
    /// negative aggro radius against a high-level player and could never be pulled at all.
    /// </remarks>
    public const float MinAggroRadius = 5.0f;

    /// <summary>How far above its own level a target's level counts, before the difference is capped.</summary>
    /// <remarks>
    /// 25 levels. Past that the radius stops shrinking, so a level 60 mob has the same tiny aggro
    /// radius for a level 85 player as for a level 80 one.
    /// </remarks>
    public const int MaxLevelDifference = 25;

    /// <summary>How far a creature may be dragged from home before it gives up. <c>CreatureLeashRadius</c>.</summary>
    public const float LeashRadius = 30.0f;

    /// <summary>
    /// How far apart in height two units may be and still aggro. <c>CREATURE_Z_ATTACK_RANGE</c>.
    /// </summary>
    /// <remarks>
    /// Checked before line of sight because it is arithmetic and line of sight is a ray cast — this
    /// runs per creature per nearby player per tick, and the cheap rejection is the point.
    /// </remarks>
    public const float MaxAggroHeightDifference = 3.0f;

    /// <summary>
    /// How far a creature will aggro <paramref name="target"/> from.
    /// </summary>
    /// <remarks>
    /// Twenty yards against an equal, moving roughly a yard per level of difference: a creature
    /// notices something weaker from further away, and something much stronger only when it is
    /// almost on top of it.
    /// <para>
    /// <b>The sign is the trap.</b> Upstream's <c>GetAggroRange</c> has its two locals named the
    /// wrong way round — the one called <c>creatureLevel</c> holds the <i>target's</i> level — so
    /// reading it literally gives a radius that grows in the wrong direction. <c>GetAttackDistance</c>
    /// computes the same thing with honest names, and this follows that one.
    /// </para>
    /// </remarks>
    public static float AggroRadius(Unit creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);

        int levelDifference = target.Level - creature.Level;
        levelDifference = Math.Max(levelDifference, -MaxLevelDifference);

        float radius = BaseAggroRadius - levelDifference;

        return Math.Clamp(radius, MinAggroRadius, MaxAggroRadius);
    }

    /// <summary>
    /// Whether a creature would start a fight with something it can see.
    /// </summary>
    /// <remarks>
    /// The caller supplies hostility and line of sight rather than this reaching for them: faction
    /// data lives in the DBC layer and line of sight in the map, and taking either as a dependency
    /// would make this untestable without both.
    /// </remarks>
    public static bool CanStartAttack(Creature creature, Unit target, bool isHostile, Func<bool> hasLineOfSight)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(hasLineOfSight);

        if (creature.React != ReactState.Aggressive || !creature.IsAlive || !target.IsAlive)
        {
            return false;
        }

        // Already busy. Re-deciding every tick would have a creature drop whatever it is fighting
        // for whoever happens to be closest.
        if (creature.Victim is not null || creature.MapId != target.MapId)
        {
            return false;
        }

        if (!isHostile)
        {
            return false;
        }

        // Height first, because it is subtraction and the line-of-sight check below is a ray cast.
        if (MathF.Abs(creature.Position.Z - target.Position.Z) > MaxAggroHeightDifference)
        {
            return false;
        }

        float radius = AggroRadius(creature, target);

        if (creature.Position.GetExactDist2dSq(target.Position) > radius * radius)
        {
            return false;
        }

        return hasLineOfSight();
    }

    /// <summary>
    /// Whether a creature has been dragged too far from home to keep fighting.
    /// </summary>
    /// <remarks>
    /// Measured from where it spawned, not from where the fight started. A creature that follows
    /// someone across a zone would otherwise never reset, which is what makes a mob trainable all
    /// the way to a city.
    /// </remarks>
    public static bool ShouldEvade(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);

        if (creature.Victim is null)
        {
            return false;
        }

        return creature.Position.GetExactDist2dSq(creature.HomePosition)
            > LeashRadius * LeashRadius;
    }

    /// <summary>
    /// Advances one creature's combat by a tick: pick a victim, chase it, or give up.
    /// </summary>
    /// <remarks>
    /// Returns what to do rather than doing it. Movement and packets belong to the map, and a
    /// creature that reached for either would be untestable without one.
    /// </remarks>
    public static AiDecision Update(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);

        if (!creature.IsAlive || creature.React == ReactState.Passive)
        {
            return default;
        }

        if (ShouldEvade(creature))
        {
            Evade(creature);

            return new AiDecision(null, creature.HomePosition, Evaded: true);
        }

        Unit? victim = creature.Threat.SelectVictim();

        if (victim is null)
        {
            // Nothing left to fight. Head home rather than standing where the fight ended.
            if (creature.Victim is not null)
            {
                Evade(creature);

                return new AiDecision(null, creature.HomePosition, Evaded: true);
            }

            return default;
        }

        if (!ReferenceEquals(creature.Victim, victim))
        {
            creature.Attack(victim);
        }

        creature.IsInCombat = true;

        // Only chase what is out of reach. Walking towards something already in melee range would
        // have the creature shuffle into the target every tick, which reads as jitter.
        if (!creature.IsWithinMeleeRange(victim))
        {
            return new AiDecision(victim, victim.Position, Evaded: false);
        }

        // In reach, so turn to face it. Not cosmetic: the swing loop refuses to attack anything
        // outside a 120° cone, so a creature that never turns is a creature that never lands a hit —
        // it stands next to its victim retrying every 100 ms forever. A chasing creature is already
        // facing the right way because its move set the orientation.
        FaceTowards(creature, victim);

        return new AiDecision(victim, null, Evaded: false);
    }

    /// <summary>
    /// Turns a unit to look at another.
    /// </summary>
    /// <remarks>
    /// Server-side only for now. The client is not sent a facing update, so a creature standing
    /// still and turning to a target that walked around it will look wrong until the next move
    /// packet — that needs the facing-target form of <c>SMSG_MONSTER_MOVE</c>, which is not written
    /// yet. The server's own orientation is what decides whether the swing lands.
    /// </remarks>
    public static void FaceTowards(Unit unit, Unit target)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(target);

        float facing = MathF.Atan2(
            target.Position.Y - unit.Position.Y,
            target.Position.X - unit.Position.X);

        unit.Position = unit.Position with { Orientation = facing };
    }

    /// <summary>Gives up on the fight: forget everyone, stop attacking, leave combat.</summary>
    /// <remarks>
    /// The threat list is cleared as well as the victim. Keeping it would have the creature
    /// re-acquire the same target the instant it got home and walk straight back out again.
    /// </remarks>
    public static void Evade(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);

        creature.Threat.Clear();
        creature.AttackStop();
        creature.IsInCombat = false;
    }

    /// <summary>
    /// Whether one unit's faction is hostile to another's.
    /// </summary>
    /// <remarks>
    /// A missing template is treated as <i>not</i> hostile. A creature with a faction the client
    /// data does not describe should stand there rather than attack everything in sight — the
    /// failure is then a creature that never fights, which is noticed, instead of a zone that
    /// attacks on sight, which reads as a game rule.
    /// </remarks>
    public static bool IsHostile(DbcStore<FactionTemplateEntry> factions, Unit attacker, Unit target)
    {
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);

        if (!factions.TryGet(attacker.FactionTemplate, out FactionTemplateEntry mine)
            || !factions.TryGet(target.FactionTemplate, out FactionTemplateEntry theirs))
        {
            return false;
        }

        return mine.IsHostileTo(theirs);
    }
}
