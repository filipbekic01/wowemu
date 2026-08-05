namespace WowEmu.Game.Combat;

/// <summary>
/// A player's own combat numbers: attack power, and what its fists are worth.
/// </summary>
/// <remarks>
/// Port of the equipment-free parts of <c>Player::UpdateAttackPowerAndDamage</c> and
/// <c>Player::CalculateMinMaxDamage</c>. Every item term is absent because items are, so what is
/// here is exactly what an unarmed character has.
/// </remarks>
public static class PlayerCombatStats
{
    /// <summary>An unarmed swing's floor. <c>BASE_MINDAMAGE</c>.</summary>
    public const float UnarmedMinDamage = 1.0f;

    /// <summary>An unarmed swing's ceiling. <c>BASE_MAXDAMAGE</c>.</summary>
    public const float UnarmedMaxDamage = 2.0f;

    /// <summary>How long an unarmed swing takes. <c>BASE_ATTACK_TIME</c>.</summary>
    public const uint UnarmedAttackTimeMs = 2000;

    /// <summary>Attack power converts to damage per second at this rate.</summary>
    public const float AttackPowerPerDps = 14.0f;

    /// <summary>Classes whose attack power comes from strength alone.</summary>
    private const byte ClassWarrior = 1;
    private const byte ClassPaladin = 2;
    private const byte ClassDeathKnight = 6;

    /// <summary>Classes that draw on both strength and agility.</summary>
    private const byte ClassHunter = 3;
    private const byte ClassRogue = 4;
    private const byte ClassShaman = 7;

    /// <summary>
    /// Melee attack power for a class, level and stats.
    /// </summary>
    /// <remarks>
    /// Three formulas, by class group. The constant subtracted at the end is what makes a level 1
    /// character's attack power small rather than large — dropping it roughly doubles every
    /// starting character's damage.
    /// </remarks>
    public static float AttackPowerFor(byte characterClass, byte level, uint strength, uint agility) =>
        characterClass switch
        {
            ClassWarrior or ClassPaladin or ClassDeathKnight => (level * 3f) + (strength * 2f) - 20f,
            ClassHunter or ClassRogue or ClassShaman => (level * 2f) + strength + agility - 20f,

            // Everything else — the casters, and druids out of form.
            _ => strength - 10f,
        };

    /// <summary>
    /// Recomputes a player's attack power and unarmed damage.
    /// </summary>
    /// <remarks>
    /// Called at login and after every level-up, because both inputs move with level.
    /// <para>
    /// <b>Without this a player swings for nothing.</b> Weapon damage and attack time are update
    /// fields that default to zero, so an unequipped character has a swing that deals no damage on
    /// a timer that never waits — every tick, for nothing. There is no error anywhere; the mob
    /// simply never dies.
    /// </para>
    /// </remarks>
    public static void Apply(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        float attackPower = MathF.Max(
            AttackPowerFor(player.Class, player.Level, player.GetStat(0), player.GetStat(1)),
            0f);

        player.AttackPower = (uint)attackPower;

        player.SetAttackTime(WeaponAttackType.BaseAttack, UnarmedAttackTimeMs);
        player.SetAttackTime(WeaponAttackType.OffAttack, UnarmedAttackTimeMs);
        player.SetAttackTime(WeaponAttackType.RangedAttack, UnarmedAttackTimeMs);

        // Attack power is a damage-per-second figure, so it is scaled by how long the swing takes
        // before being added. Adding it raw makes a slow weapon and a fast one hit for the same.
        float fromAttackPower = attackPower / AttackPowerPerDps * (UnarmedAttackTimeMs / 1000f);

        player.MinDamage = UnarmedMinDamage + fromAttackPower;
        player.MaxDamage = UnarmedMaxDamage + fromAttackPower;
    }
}
