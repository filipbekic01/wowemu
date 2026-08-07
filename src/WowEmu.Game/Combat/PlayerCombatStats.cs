using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.Game.Combat;

/// <summary>
/// A player's own combat numbers: attack power, and what its fists are worth.
/// </summary>
/// <remarks>
/// Port of <c>Player::UpdateAttackPowerAndDamage</c> and <c>Player::CalculateMinMaxDamage</c>,
/// less the aura and rating terms.
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

        ApplyStats(player);

        float attackPower = MathF.Max(
            AttackPowerFor(player.Class, player.Level, player.GetStat(0), player.GetStat(1)),
            0f);

        player.AttackPower = (uint)attackPower;

        ItemTemplate? mainHand = WeaponIn(player, InventorySlots.MainHand);
        ItemTemplate? offHand = WeaponIn(player, InventorySlots.OffHand);
        ItemTemplate? ranged = WeaponIn(player, InventorySlots.Ranged);

        uint swingMs = mainHand?.Delay > 0 ? mainHand.Delay : UnarmedAttackTimeMs;

        player.SetAttackTime(WeaponAttackType.BaseAttack, swingMs);
        player.SetAttackTime(
            WeaponAttackType.OffAttack, offHand?.Delay > 0 ? offHand.Delay : UnarmedAttackTimeMs);
        player.SetAttackTime(
            WeaponAttackType.RangedAttack, ranged?.Delay > 0 ? ranged.Delay : UnarmedAttackTimeMs);

        // Attack power is a damage-per-second figure, so it is scaled by how long the swing takes
        // before being added. Adding it raw makes a slow weapon and a fast one hit for the same —
        // and it is the weapon's own speed that scales it, not the unarmed 2000 ms.
        float fromAttackPower = attackPower / AttackPowerPerDps * (swingMs / 1000f);

        (float weaponMin, float weaponMax) = WeaponDamage(mainHand);

        player.MinDamage = weaponMin + fromAttackPower;
        player.MaxDamage = weaponMax + fromAttackPower;

        player.Armor = ArmorFromEquipment(player);

        ApplyResistances(player);
        ApplyRatings(player);
    }

    /// <summary>
    /// Writes the five attribute fields as the level's base plus everything worn.
    /// </summary>
    /// <remarks>
    /// Recomputed from the base every time rather than adjusted by a delta on equip and unequip.
    /// A delta is one missed call away from a character who gains three strength every time they
    /// take a belt off, and nothing would ever notice.
    /// <para>
    /// The stat <i>type</i> column is an <c>ItemModType</c>. The five attributes land here; the
    /// combat ratings go to <see cref="ApplyRatings"/>, and the flat spell power and regen terms are
    /// still read and ignored.
    /// </para>
    /// <para>
    /// Auras are summed in on top, and <b>they sum rather than taking the strongest</b> — unlike
    /// speed modifiers, which do the opposite. Two stat buffs stack; two slows do not. The rule is
    /// per aura type upstream, not a house style.
    /// </para>
    /// </remarks>
    private static void ApplyStats(Player player)
    {
        PlayerBaseStats stats = player.BaseStats;

        Span<int> totals =
        [
            (int)stats.Strength,
            (int)stats.Agility,
            (int)stats.Stamina,
            (int)stats.Intellect,
            (int)stats.Spirit,
        ];

        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.Equipped(slot) is not { IsBroken: false } item)
            {
                continue;
            }

            // Only the declared count, not all ten: the columns past it hold leftovers.
            int declared = Math.Min((int)item.Template.StatsCount, ItemConstants.MaxStats);

            for (int i = 0; i < declared; i++)
            {
                ItemStat stat = item.Template.Stats[i];
                int index = AttributeIndex(stat.Type);

                if (index >= 0)
                {
                    totals[index] += stat.Value;
                }
            }
        }

        for (int i = 0; i < totals.Length; i++)
        {
            // A stored MiscValue of -1 means every attribute at once — Mark of the Wild — and the
            // container matches it against whichever index is asked for.
            totals[i] += player.Auras.Total(AuraType.ModStat, miscValue: i);

            player.SetStat(i, (uint)Math.Max(totals[i], 0));
        }
    }

    /// <summary>
    /// Which of the five attribute fields an <c>ItemModType</c> feeds, or -1.
    /// </summary>
    /// <remarks>
    /// <b>Type 0 is mana, not strength.</b> The five attributes start at 3, and reading the type as
    /// a stat index directly puts every item's mana bonus into strength.
    /// </remarks>
    private static int AttributeIndex(byte statType) => statType switch
    {
        ItemModStrength => 0,
        ItemModAgility => 1,
        ItemModStamina => 2,
        ItemModIntellect => 3,
        ItemModSpirit => 4,
        _ => -1,
    };

    /// <summary><c>ItemModType</c>, the five that map onto attributes.</summary>
    private const byte ItemModAgility = 3;
    private const byte ItemModStrength = 4;
    private const byte ItemModIntellect = 5;
    private const byte ItemModSpirit = 6;
    private const byte ItemModStamina = 7;

    /// <summary>
    /// The damage range of a weapon, or of a bare fist.
    /// </summary>
    /// <remarks>
    /// Only the first of an item's two damage ranges counts here. Upstream sums both for the
    /// tooltip's damage-per-second and uses the first for the swing; a weapon whose second range is
    /// a different school is a spell effect, not part of the physical hit.
    /// </remarks>
    private static (float Min, float Max) WeaponDamage(ItemTemplate? weapon)
    {
        if (weapon is null || weapon.Damage[0].Max <= 0f)
        {
            return (UnarmedMinDamage, UnarmedMaxDamage);
        }

        return (weapon.Damage[0].Min, weapon.Damage[0].Max);
    }

    /// <summary>What is worn in a slot, if it is a weapon and not broken.</summary>
    /// <remarks>
    /// A broken weapon gives none of its stats — that is what repairing is for — and swinging one
    /// is the same as swinging a fist.
    /// </remarks>
    private static ItemTemplate? WeaponIn(Player player, byte slot) =>
        player.Inventory.Equipped(slot) is { IsBroken: false } item ? item.Template : null;

    /// <summary>
    /// Armour from everything worn.
    /// </summary>
    /// <remarks>
    /// The sum of the equipped items' <c>armor</c> columns, and nothing else: a player has no base
    /// armour of its own in 3.3.5a, and agility's contribution is a class-scaled term that needs
    /// <c>gtChanceToMeleeCrit</c> and friends. That is a gap, not a simplification — a level-1
    /// character's agility armour is small, and a level-80 rogue's is not.
    /// </remarks>
    /// <summary>
    /// Writes the six magic resistances from what is worn.
    /// </summary>
    /// <remarks>
    /// They share a field block with armour: <c>UNIT_FIELD_RESISTANCES</c> is seven entries and slot
    /// 0 is <i>armour</i>, which is the physical school. Writing holy into slot 0 overwrites the
    /// armour that <see cref="ArmorFromEquipment"/> just computed, and the character loses every
    /// point of mitigation while gaining holy resistance nobody asked for.
    /// <para>
    /// These come from the item's own columns rather than its stat list — resistances are not
    /// <c>ItemModType</c>s, which is why an item can carry both.
    /// </para>
    /// </remarks>
    private static void ApplyResistances(Player player)
    {
        Span<uint> totals = stackalloc uint[SpellSchools];

        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.Equipped(slot) is not { IsBroken: false } item)
            {
                continue;
            }

            ItemTemplate template = item.Template;

            totals[SchoolHoly] += template.HolyResistance;
            totals[SchoolFire] += template.FireResistance;
            totals[SchoolNature] += template.NatureResistance;
            totals[SchoolFrost] += template.FrostResistance;
            totals[SchoolShadow] += template.ShadowResistance;
            totals[SchoolArcane] += template.ArcaneResistance;
        }

        // From one, not zero: slot 0 is armour and is written elsewhere.
        for (int school = SchoolHoly; school < SpellSchools; school++)
        {
            player.Fields.SetUInt32(UpdateFields.UNIT_FIELD_RESISTANCES + school, totals[school]);
        }
    }

    /// <summary>
    /// Writes the twenty-five combat ratings from what is worn.
    /// </summary>
    /// <remarks>
    /// Port of the rating half of <c>Player::_ApplyItemMods</c>. The mapping is not one-to-one:
    /// <b>several item mods fan out to three ratings each</b> — a "hit rating" on an item feeds
    /// melee, ranged <i>and</i> spell hit, and resilience feeds all three crit-taken ratings.
    /// Treating them as single ratings loses two thirds of what the item says it gives.
    /// <para>
    /// <b>These are stored ratings, not percentages.</b> The client shows the rating and works out
    /// the percentage itself; the server turning a rating into a chance needs
    /// <c>gtCombatRatings.dbc</c> and its class scalar, which is the separate gap already recorded.
    /// So a crit rating shows correctly on the character sheet and does not yet change a roll.
    /// </para>
    /// </remarks>
    private static void ApplyRatings(Player player)
    {
        Span<int> totals = stackalloc int[CombatRatings];

        totals.Clear();

        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.Equipped(slot) is not { IsBroken: false } item)
            {
                continue;
            }

            int declared = Math.Min((int)item.Template.StatsCount, ItemConstants.MaxStats);

            for (int i = 0; i < declared; i++)
            {
                ItemStat stat = item.Template.Stats[i];

                foreach (int rating in RatingsFor(stat.Type))
                {
                    totals[rating] += stat.Value;
                }
            }
        }

        for (int rating = 0; rating < CombatRatings; rating++)
        {
            player.Fields.SetUInt32(
                UpdateFields.PLAYER_FIELD_COMBAT_RATING_1 + rating, (uint)Math.Max(totals[rating], 0));
        }
    }

    /// <summary>
    /// Which combat ratings an <c>ItemModType</c> feeds. Empty for anything that feeds none.
    /// </summary>
    /// <remarks>
    /// The three-way fan-outs are the whole reason this returns a list. <c>ITEM_MOD_HIT_RATING</c>
    /// is not "the hit rating" — there is no such field — it is melee, ranged and spell hit at once.
    /// </remarks>
    private static ReadOnlySpan<int> RatingsFor(byte statType) => statType switch
    {
        12 => [CrDefenseSkill],
        13 => [CrDodge],
        14 => [CrParry],
        15 => [CrBlock],
        16 => [CrHitMelee],
        17 => [CrHitRanged],
        18 => [CrHitSpell],
        19 => [CrCritMelee],
        20 => [CrCritRanged],
        21 => [CrCritSpell],
        22 => [CrHitTakenMelee],
        23 => [CrHitTakenRanged],
        24 => [CrHitTakenSpell],
        25 => [CrCritTakenMelee],
        26 => [CrCritTakenRanged],
        27 => [CrCritTakenSpell],
        28 => [CrHasteMelee],
        29 => [CrHasteRanged],
        30 => [CrHasteSpell],

        // The combined forms, each feeding all three schools at once.
        31 => [CrHitMelee, CrHitRanged, CrHitSpell],
        32 => [CrCritMelee, CrCritRanged, CrCritSpell],
        33 => [CrHitTakenMelee, CrHitTakenRanged, CrHitTakenSpell],

        // Resilience and crit-taken are the same three ratings — upstream falls one case into the
        // other, which is easy to read as a missing break.
        34 or 35 => [CrCritTakenMelee, CrCritTakenRanged, CrCritTakenSpell],

        36 => [CrHasteMelee, CrHasteRanged, CrHasteSpell],
        37 => [CrExpertise],
        44 => [CrArmorPenetration],
        _ => [],
    };

    /// <summary><c>MAX_SPELL_SCHOOL</c>, and the six that are not armour.</summary>
    private const int SpellSchools = 7;
    private const int SchoolHoly = 1;
    private const int SchoolFire = 2;
    private const int SchoolNature = 3;
    private const int SchoolFrost = 4;
    private const int SchoolShadow = 5;
    private const int SchoolArcane = 6;

    /// <summary><c>CombatRating</c> and <c>MAX_COMBAT_RATING</c>.</summary>
    private const int CombatRatings = 25;
    private const int CrDefenseSkill = 1;
    private const int CrDodge = 2;
    private const int CrParry = 3;
    private const int CrBlock = 4;
    private const int CrHitMelee = 5;
    private const int CrHitRanged = 6;
    private const int CrHitSpell = 7;
    private const int CrCritMelee = 8;
    private const int CrCritRanged = 9;
    private const int CrCritSpell = 10;
    private const int CrHitTakenMelee = 11;
    private const int CrHitTakenRanged = 12;
    private const int CrHitTakenSpell = 13;
    private const int CrCritTakenMelee = 14;
    private const int CrCritTakenRanged = 15;
    private const int CrCritTakenSpell = 16;
    private const int CrHasteMelee = 17;
    private const int CrHasteRanged = 18;
    private const int CrHasteSpell = 19;
    private const int CrExpertise = 23;
    private const int CrArmorPenetration = 24;

    private static uint ArmorFromEquipment(Player player)
    {
        uint armor = 0;

        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.Equipped(slot) is { IsBroken: false } item)
            {
                armor += item.Template.Armor;
            }
        }

        return armor;
    }
}
