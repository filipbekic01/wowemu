namespace WowEmu.Game.Combat;

/// <summary>What the client is told the victim did about the swing. <c>VictimState</c>.</summary>
public enum VictimState : byte
{
    /// <summary>Untouched — the attacker missed.</summary>
    Intact = 0,

    /// <summary>Took a hit, clean or blocked.</summary>
    Hit = 1,

    Dodge = 2,
    Parry = 3,
    Interrupt = 4,

    /// <summary>Blocked the whole hit.</summary>
    Blocks = 5,

    Evades = 6,
    IsImmune = 7,
    Deflects = 8,
}

/// <summary>Flags on a melee swing. <c>HitInfo</c>.</summary>
/// <remarks>Only the bits an auto-attack sets; the enum has around thirty in total.</remarks>
[Flags]
public enum HitInfo : uint
{
    NormalSwing = 0x00000000,
    AffectsVictim = 0x00000002,
    OffHand = 0x00000004,
    Miss = 0x00000010,
    CriticalHit = 0x00000200,
    Block = 0x00002000,
    Glancing = 0x00010000,
    Crushing = 0x00020000,
    SwingNoHitSound = 0x00200000,
}

/// <summary>
/// The result of one melee swing, ready for the client and for the systems that follow.
/// </summary>
/// <param name="Outcome">Which row of the attack table the swing landed on.</param>
/// <param name="Damage">What the victim actually loses.</param>
/// <param name="CleanDamage">
/// What was prevented — the difference between the pre-mitigation hit and <paramref name="Damage"/>.
/// Rage and threat are paid on this as well as on damage dealt, which is why a fully parried swing
/// still generates both.
/// </param>
/// <param name="BlockedAmount">How much of the hit the victim's shield ate.</param>
/// <param name="HitInfo">Flags for the client.</param>
/// <param name="VictimState">What the client draws the victim doing.</param>
public readonly record struct MeleeDamageInfo(
    MeleeHitOutcome Outcome,
    uint Damage,
    uint CleanDamage,
    uint BlockedAmount,
    HitInfo HitInfo,
    VictimState VictimState);

/// <summary>
/// Turns a rolled swing into a number.
/// </summary>
/// <remarks>
/// Port of the outcome switch in <c>Unit::CalculateMeleeDamage</c>.
/// <para>
/// <b>Armour comes first, then the outcome multiplier.</b> Upstream mitigates the raw weapon damage
/// and only then doubles it for a crit. Applying the multiplier first and mitigating afterwards is
/// algebraically the same for a plain multiplier but not for block, which subtracts a flat amount —
/// and it is not the same at all once the <c>ceil</c> in armour mitigation is in the middle.
/// </para>
/// <para>
/// One damage slot, not two. A weapon can carry a second damage school, and upstream loops over both
/// — but only for players, and only from an item's stats. There are no items yet, so a loop over one
/// element would be structure without substance; it goes in with equipment.
/// </para>
/// </remarks>
public static class MeleeDamage
{
    /// <summary>A crushing blow lands for 150 %.</summary>
    /// <remarks>Applied as <c>damage + damage / 2</c> on integers, so the half is truncated.</remarks>
    public const float CrushingMultiplier = 1.5f;

    /// <summary>A critical strike lands for 200 %.</summary>
    public const uint CritMultiplier = 2;

    /// <summary>Glancing loses 10 % per level of difference, up to three levels.</summary>
    public const int MaxGlancingLevelDifference = 3;

    /// <summary>
    /// Applies an outcome to an already-mitigated hit.
    /// </summary>
    /// <param name="outcome">What the attack table rolled.</param>
    /// <param name="mitigatedDamage">The hit after armour, before the outcome is applied.</param>
    /// <param name="attackerLevel">Used by glancing, which scales on the level difference.</param>
    /// <param name="victimLevel">As above.</param>
    /// <param name="blockValue">The victim's shield block value; ignored unless the swing was blocked.</param>
    /// <param name="isOffHand">Sets the off-hand flag for the client.</param>
    public static MeleeDamageInfo Apply(
        MeleeHitOutcome outcome,
        uint mitigatedDamage,
        uint attackerLevel,
        uint victimLevel,
        uint blockValue = 0,
        bool isOffHand = false)
    {
        HitInfo hitInfo = isOffHand ? HitInfo.OffHand : HitInfo.NormalSwing;
        VictimState victimState;
        uint damage = mitigatedDamage;
        uint cleanDamage = 0;
        uint blocked = 0;

        switch (outcome)
        {
            case MeleeHitOutcome.Evade:
                // An evading creature takes nothing and generates nothing — no clean damage either,
                // so no rage and no threat. That is what makes an evade a wasted swing rather than a
                // free one.
                hitInfo |= HitInfo.Miss | HitInfo.SwingNoHitSound;
                victimState = VictimState.Evades;
                damage = 0;
                break;

            case MeleeHitOutcome.Miss:
                hitInfo |= HitInfo.Miss;
                victimState = VictimState.Intact;
                damage = 0;
                break;

            case MeleeHitOutcome.Normal:
                victimState = VictimState.Hit;
                break;

            case MeleeHitOutcome.Crit:
                hitInfo |= HitInfo.CriticalHit;
                victimState = VictimState.Hit;
                damage *= CritMultiplier;
                break;

            case MeleeHitOutcome.Dodge:
            case MeleeHitOutcome.Parry:
                // Nothing lands, but the whole swing counts as clean damage: a dodged or parried hit
                // still gives the attacker rage and still puts the attacker on the threat table.
                victimState = outcome == MeleeHitOutcome.Dodge ? VictimState.Dodge : VictimState.Parry;
                cleanDamage = damage;
                damage = 0;
                break;

            case MeleeHitOutcome.Block:
                // Block subtracts a flat amount rather than a fraction, so the same shield is
                // absolute protection against a weak hit and a rounding error against a strong one.
                hitInfo |= HitInfo.Block;
                blocked = blockValue;

                if (blocked >= damage)
                {
                    // The shield ate all of it. `blocked` reports what was actually stopped, not the
                    // shield's full value, or the client draws a bigger number than the hit.
                    blocked = damage;
                    cleanDamage = damage;
                    damage = 0;
                    victimState = VictimState.Blocks;
                }
                else
                {
                    cleanDamage = blocked;
                    damage -= blocked;
                    victimState = VictimState.Hit;
                }

                break;

            case MeleeHitOutcome.Glancing:
                {
                    hitInfo |= HitInfo.Glancing;
                    victimState = VictimState.Hit;

                    // Ten percent per level, but never more than three levels' worth — so the floor
                    // is 70 %, however far above the attacker the target is.
                    int levelDifference = (int)victimLevel - (int)attackerLevel;
                    levelDifference = Math.Min(levelDifference, MaxGlancingLevelDifference);

                    float keptFraction = 1f - (levelDifference * 0.1f);
                    uint reduced = (uint)(keptFraction * damage);

                    cleanDamage = damage - reduced;
                    damage = reduced;
                    break;
                }

            case MeleeHitOutcome.Crushing:
                hitInfo |= HitInfo.Crushing;
                victimState = VictimState.Hit;

                // Integer halving, so an odd hit crushes for one less than 150 % exactly.
                damage += damage / 2;
                break;

            default:
                victimState = VictimState.Hit;
                break;
        }

        // Everything that is not a miss affects the victim, including a swing reduced to zero.
        if (!hitInfo.HasFlag(HitInfo.Miss))
        {
            hitInfo |= HitInfo.AffectsVictim;
        }

        return new MeleeDamageInfo(outcome, damage, cleanDamage, blocked, hitInfo, victimState);
    }
}
