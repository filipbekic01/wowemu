namespace WowEmu.Data.Client;

/// <summary>A row of <c>ChrRaces.dbc</c>.</summary>
/// <remarks>
/// The display ids are why this store is on the critical path for entering the world: they are the
/// model the client draws. A character whose race has no row renders as nothing at all.
/// </remarks>
public sealed record ChrRacesEntry(
    uint RaceId,
    uint Flags,
    uint FactionId,
    uint MaleDisplayId,
    uint FemaleDisplayId,
    uint TeamId,
    uint CinematicSequenceId,
    uint Alliance,
    string Name,
    uint Expansion)
{
    /// <summary>Alliance races report team 7; everything else is Horde. Upstream's encoding.</summary>
    public bool IsAlliance => TeamId == 7;
}


/// <summary>
/// A row of <c>FactionTemplate.dbc</c> — who a unit will and will not fight.
/// </summary>
/// <remarks>
/// Every creature and every player carries a faction <i>template</i> id, not a faction id. The
/// template is the relationship table: which factions it counts as enemies, which as friends, and
/// two bitmasks for everyone it has no specific opinion about.
/// </remarks>
/// <param name="Id">The template id, which is what <c>creature_template.faction</c> holds.</param>
/// <param name="Faction">The faction this template belongs to, for reputation.</param>
/// <param name="Flags">Template flags — contested guards and call-for-help live here.</param>
/// <param name="OurMask">Which broad groups this unit belongs to. <c>m_factionGroup</c>.</param>
/// <param name="FriendlyMask">Which broad groups it is friendly towards.</param>
/// <param name="HostileMask">Which broad groups it is hostile towards.</param>
/// <param name="EnemyFactions">Up to four specific factions it will always fight.</param>
/// <param name="FriendFactions">Up to four specific factions it will never fight.</param>
public sealed record FactionTemplateEntry(
    uint Id,
    uint Faction,
    uint Flags,
    uint OurMask,
    uint FriendlyMask,
    uint HostileMask,
    uint[] EnemyFactions,
    uint[] FriendFactions)
{
    /// <summary>How many specific relations a template can name in each direction.</summary>
    public const int MaxFactionRelations = 4;

    /// <summary>The player faction groups, from <c>FactionMasks</c>.</summary>
    public const uint MaskPlayer = 1;
    public const uint MaskAlliance = 2;
    public const uint MaskHorde = 4;
    public const uint MaskMonster = 8;

    /// <summary>
    /// Whether this unit will attack <paramref name="other"/> on sight.
    /// </summary>
    /// <remarks>
    /// <b>The specific lists win over the masks, and enemies are checked before friends.</b> A
    /// template can name a faction as an enemy while its mask says the whole group is fine, which is
    /// how a guard is hostile to one enemy city but not to neutral travellers. Checking the mask
    /// first would make every such exception disappear.
    /// <para>
    /// Note the asymmetry with <see cref="IsFriendlyTo"/>: hostility consults only
    /// <see cref="HostileMask"/> against the other's <see cref="OurMask"/>, in one direction, while
    /// friendliness checks both directions. Two units can therefore be neither hostile nor friendly
    /// — which is exactly what neutral means.
    /// </para>
    /// </remarks>
    public bool IsHostileTo(FactionTemplateEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Faction != 0)
        {
            if (Array.IndexOf(EnemyFactions, other.Faction) >= 0)
            {
                return true;
            }

            if (Array.IndexOf(FriendFactions, other.Faction) >= 0)
            {
                return false;
            }
        }

        return (HostileMask & other.OurMask) != 0;
    }

    /// <summary>Whether this unit counts <paramref name="other"/> as a friend.</summary>
    /// <remarks>Sharing a faction is always friendly, whatever the masks say.</remarks>
    public bool IsFriendlyTo(FactionTemplateEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Faction == other.Faction)
        {
            return true;
        }

        if (other.Faction != 0)
        {
            if (Array.IndexOf(EnemyFactions, other.Faction) >= 0)
            {
                return false;
            }

            if (Array.IndexOf(FriendFactions, other.Faction) >= 0)
            {
                return true;
            }
        }

        return (FriendlyMask & other.OurMask) != 0 || (OurMask & other.FriendlyMask) != 0;
    }

    /// <summary>Hostile to players of either side.</summary>
    public bool IsHostileToPlayers => (HostileMask & MaskPlayer) != 0;

    /// <summary>
    /// Picks a fight with nobody at all — critters, and most quest props.
    /// </summary>
    /// <remarks>
    /// Distinct from merely not being hostile to you: a neutral-to-all unit never initiates, which
    /// is what stops a field of rabbits mobbing anyone who walks past.
    /// </remarks>
    public bool IsNeutralToAll =>
        HostileMask == 0 && FriendlyMask == 0 && Array.TrueForAll(EnemyFactions, faction => faction == 0);
}

/// <summary>
/// A row of <c>WorldSafeLocs.dbc</c> — a graveyard, or any other named safe point.
/// </summary>
/// <remarks>
/// <b>This is where graveyard coordinates live in our data.</b> Newer AzerothCore reads them from a
/// <c>game_graveyard</c> world table instead; the vendored dump predates that and carries only
/// <c>game_graveyard_zone</c>, which maps a zone to an id in <i>this</i> file. Same divergence as
/// <c>creature_template_model</c> — read the C++ for behaviour, but check the dump before trusting a
/// column name.
/// </remarks>
/// <summary>
/// One row of <c>QuestXP.dbc</c>: what a quest of this level pays, by difficulty band.
/// </summary>
/// <remarks>
/// The id <i>is</i> the quest level, which is why a quest's <c>RewardXPId</c> column is a column
/// index into this row rather than a row id. Looking the quest up by its own id finds nothing.
/// </remarks>
public sealed record QuestXpEntry(uint Level, uint[] ByDifficulty)
{
    /// <summary>How many difficulty columns each row has.</summary>
    public const int DifficultyCount = 10;

    /// <summary>The payout for one difficulty band. Out-of-range bands pay nothing.</summary>
    public uint For(byte difficulty) =>
        difficulty < ByDifficulty.Length ? ByDifficulty[difficulty] : 0;
}

/// <summary>
/// A row of <c>AreaTable.dbc</c>: one zone, or one subzone of a zone.
/// </summary>
/// <param name="Id">The area id, which is what terrain tiles store per chunk.</param>
/// <param name="MapId">Which map it is on.</param>
/// <param name="ParentZoneId">
/// <b>Zero means this row IS a zone.</b> Otherwise it names the zone this subzone belongs to.
/// Elwynn Forest is a zone and stores 0; Northshire Valley is a subzone of it and stores 12.
/// </param>
/// <param name="Flags">Sanctuary, capital city, and the rest. 312 for every city upstream notes.</param>
/// <param name="AreaLevel">The suggested level, or 0 where there is none.</param>
/// <param name="Name">The name the client shows.</param>
/// <param name="Team">Alliance, Horde or neither, for the areas that belong to one.</param>
/// <param name="LiquidTypeOverride">
/// Four entries, one per liquid sound bank, letting a zone substitute its own liquid — which is how
/// Naxxramas gets slime where the geometry says water. Zero means no override for that kind.
/// </param>
public sealed record AreaTableEntry(
    uint Id,
    uint MapId,
    uint ParentZoneId,
    uint Flags,
    int AreaLevel,
    string Name,
    uint Team,
    uint[] LiquidTypeOverride)
{
    /// <summary>How many liquid kinds a zone can override. One per sound bank.</summary>
    public const int LiquidOverrideCount = 4;

    /// <summary>Whether this row is a zone in its own right rather than part of one.</summary>
    public bool IsZone => ParentZoneId == 0;

    /// <summary>The override for one liquid kind, or 0.</summary>
    public uint OverrideFor(uint soundBank) =>
        soundBank < (uint)LiquidTypeOverride.Length ? LiquidTypeOverride[soundBank] : 0;
}

/// <summary>
/// A row of <c>DurabilityCosts.dbc</c>: what a point of durability costs at one item level.
/// </summary>
/// <remarks>
/// Twenty-nine multipliers per row, indexed by item class and subclass — weapons take the subclass
/// directly, armour takes it plus 21, and everything else uses slot zero. The row id is the
/// <i>item level</i>, not an item id.
/// </remarks>
public sealed record DurabilityCostsEntry(uint ItemLevel, uint[] Multipliers)
{
    /// <summary>How many multipliers a row carries.</summary>
    public const int MultiplierCount = 29;

    /// <summary>Item classes, from <c>ItemClass</c>.</summary>
    public const byte ClassWeapon = 2;
    public const byte ClassArmor = 4;

    /// <summary>
    /// Which multiplier an item's class and subclass select.
    /// </summary>
    /// <remarks>
    /// Port of <c>ItemSubClassToDurabilityMultiplierId</c>. Armour's <c>+ 21</c> is the whole of the
    /// mapping and is easy to miss — without it a plate chestpiece is priced as a dagger.
    /// </remarks>
    public static int MultiplierFor(byte itemClass, byte subClass) => itemClass switch
    {
        ClassWeapon => subClass,
        ClassArmor => subClass + 21,
        _ => 0,
    };

    /// <summary>The multiplier for an item, or 0 when the index is out of range.</summary>
    public uint For(byte itemClass, byte subClass)
    {
        int index = MultiplierFor(itemClass, subClass);

        return index >= 0 && index < Multipliers.Length ? Multipliers[index] : 0;
    }
}

/// <summary>
/// A row of <c>DurabilityQuality.dbc</c>: how much an item's quality scales its repair bill.
/// </summary>
/// <remarks>
/// <b>The row id is not the quality.</b> Upstream looks it up as <c>(quality + 1) * 2</c>, so a
/// common item reads row 4 and an epic row 10. Indexing by the quality itself finds a row that
/// exists and is wrong, which is the worst kind of off-by-one.
/// </remarks>
public sealed record DurabilityQualityEntry(uint Id, float Modifier);

/// <summary>
/// A row of <c>QuestFactionReward.dbc</c> — the ten reputation amounts a quest can pay.
/// </summary>
/// <remarks>
/// <b>Two rows in the whole file.</b> Row 1 holds the gains and row 2 the identical losses, so a
/// quest's <c>RewardFactionValueID</c> picks the row by its SIGN and the column by its magnitude.
/// Reading the id as an amount pays 1 point where the table means 10, or 5 where it means 250.
/// </remarks>
public sealed record QuestFactionRewardEntry(uint Id, int[] Values)
{
    /// <summary>How many amounts a row carries.</summary>
    public const int Count = 10;

    /// <summary>The amount at a column, or zero when it is out of range.</summary>
    public int At(int index) => index >= 0 && index < Values.Length ? Values[index] : 0;
}

/// <summary>
/// A row of <c>Faction.dbc</c> — one faction a character can have standing with.
/// </summary>
/// <remarks>
/// <b><see cref="ReputationListId"/> is not the faction id.</b> It is the slot in the client's own
/// 128-entry reputation block, and <b>most factions do not have one</b> — those are tracked but
/// never shown. Writing standing by faction id runs off the end of the field block.
/// </remarks>
public sealed record FactionEntry(uint Id, int ReputationListId, uint ParentFactionId, string Name);

/// <summary>
/// A row of <c>CharTitles.dbc</c> — one title a character can wear.
/// </summary>
/// <remarks>
/// <b><see cref="BitIndex"/> is not the id.</b> The id is what a quest names; the bit index is
/// where the title sits in the client's 128-bit known-titles mask, and the two are unrelated
/// numbers. Setting the bit for the id grants a title nobody asked for.
/// </remarks>
public sealed record CharTitleEntry(uint Id, uint BitIndex, string Name);

/// <summary>What one enchantment does. <c>ItemEnchantmentType</c>.</summary>
public static class EnchantmentEffect
{
    public const uint None = 0;
    public const uint CombatSpell = 1;
    public const uint Damage = 2;
    public const uint EquipSpell = 3;
    public const uint Resistance = 4;
    public const uint Stat = 5;
    public const uint Totem = 6;
    public const uint UseSpell = 7;
    public const uint PrismaticSocket = 8;
}

/// <summary>
/// A row of <c>SpellItemEnchantment.dbc</c> — one enchantment an item can carry.
/// </summary>
/// <remarks>
/// <b>Three effects per row, and each has its own type.</b> One enchantment can add a stat and a
/// proc at once; reading only the first effect silently drops half of what many enchants do.
/// </remarks>
public sealed record SpellItemEnchantmentEntry(
    uint Id,
    uint Charges,
    uint[] Types,
    int[] Amounts,
    uint[] SpellIds,
    uint AuraId,
    uint Slot,
    uint GemItemId,
    uint RequiredSkill,
    uint RequiredSkillValue,
    uint RequiredLevel,
    string Name)
{
    /// <summary>How many effects a row carries. <c>MAX_SPELL_ITEM_ENCHANTMENT_EFFECTS</c>.</summary>
    public const int Effects = 3;
}

/// <summary>
/// A row of <c>ItemRandomProperties.dbc</c> — the "of the Bear" suffixes with fixed amounts.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ItemRandomSuffixEntry"/>, and the distinction is the whole of the
/// system: a <b>positive</b> RandomProperty id means this table and its fixed enchantments, while a
/// <b>negative</b> one means the suffix table and its scaled ones. Reading the sign wrong looks up
/// the wrong table entirely.
/// </remarks>
public sealed record ItemRandomPropertiesEntry(uint Id, uint[] Enchantments, string Name)
{
    /// <summary>How many enchantments a row carries. <c>MAX_ITEM_ENCHANTMENT_EFFECTS</c>.</summary>
    public const int Enchants = 5;
}

/// <summary>
/// A row of <c>ItemRandomSuffix.dbc</c> — suffixes whose amounts scale with the item.
/// </summary>
/// <remarks>
/// <b>The allocation percentages are in ten-thousandths.</b> They multiply the item's suffix factor
/// and are divided by 10,000, so using them raw gives ten thousand times too much of every stat.
/// </remarks>
/// <summary>
/// A row of <c>RandPropPoints.dbc</c> — how many stat points a random suffix is worth.
/// </summary>
/// <remarks>
/// <b>Keyed by item level, not by an id of its own.</b> The first column is the item level, so the
/// lookup is by the item's level and there is nothing else to join on.
/// <para>
/// Five figures per quality, indexed by a slot coefficient rather than by the inventory type
/// directly — a chest and a two-hander share coefficient 0, and a ring and a cloak share 2.
/// </para>
/// </remarks>
public sealed record RandomPropertyPointsEntry(
    uint ItemLevel, uint[] Epic, uint[] Rare, uint[] Uncommon)
{
    /// <summary>How many slot coefficients there are.</summary>
    public const int Coefficients = 5;

    /// <summary>
    /// The points for a quality, or zero for one that has no random properties.
    /// </summary>
    /// <param name="quality">
    /// <c>ItemQuality</c> from the Db layer — uncommon, rare and epic are the only three that carry
    /// points at all.
    /// </param>
    public uint For(uint quality, int coefficient)
    {
        if (coefficient < 0 || coefficient >= Coefficients)
        {
            return 0;
        }

        // The quality constants live in the Db layer with the item template that carries them;
        // duplicating them here would be two records of the same fact.
        return quality switch
        {
            2 => Uncommon[coefficient],
            3 => Rare[coefficient],
            4 => Epic[coefficient],

            // Legendary and artifact have no random properties at all, and neither do the greys.
            _ => 0,
        };
    }
}

public sealed record ItemRandomSuffixEntry(
    uint Id, uint[] Enchantments, uint[] AllocationPct, string Name)
{
    /// <inheritdoc cref="ItemRandomPropertiesEntry.Enchants"/>
    public const int Enchants = 5;
}

/// <summary>
/// A row of <c>Lock.dbc</c> — what it takes to open something.
/// </summary>
/// <remarks>
/// Eight independent cases, and <b>any one of them is enough</b>. A chest can be opened with the
/// right key or picked with enough lockpicking, and requiring all eight would make almost every
/// locked thing in the game impossible.
/// </remarks>
public sealed record LockEntry(uint Id, uint[] Types, uint[] Indices, uint[] Skills)
{
    /// <summary>How many ways one lock can list. <c>MAX_LOCK_CASE</c>.</summary>
    public const int Cases = 8;

    /// <summary>Nothing at all. <c>LOCK_KEY_NONE</c>.</summary>
    public const uint KeyNone = 0;

    /// <summary>A specific item — a key. The index is its item id.</summary>
    public const uint KeyItem = 1;

    /// <summary>A skill at a value. The index is a <c>LockType</c>, not a skill id.</summary>
    public const uint KeySkill = 2;
}

/// <summary>
/// A row of <c>BankBagSlotPrices.dbc</c> — what the next bank bag slot costs.
/// </summary>
/// <remarks>
/// Indexed by the slot being bought, 1-based, so buying your first costs row 1. Twelve rows exist
/// but only seven slots do: rows 8 to 12 carry a sentinel price of 999,999,999 copper, which is
/// most of the client's own money ceiling. They are placeholders, not slots.
/// </remarks>
public sealed record BankBagSlotPriceEntry(uint Slot, uint Price);

/// <summary>
/// A row of <c>ItemLimitCategory.dbc</c> — how many of a <i>family</i> of items may be held.
/// </summary>
/// <remarks>
/// Separate from an item's own <c>MaxCount</c> because the limit is shared across different items:
/// the "Mana Gems" category caps you at one, however many kinds of mana gem exist. Without this
/// table each gem would be capped separately and you could carry one of each.
/// </remarks>
public sealed record ItemLimitCategoryEntry(uint Id, uint MaxCount, uint Mode)
{
    /// <summary>The cap is on how many you may HOLD. <c>ITEM_LIMIT_CATEGORY_MODE_HAVE</c>.</summary>
    public const uint ModeHave = 0;

    /// <summary>The cap is on how many you may WEAR — holding more is fine.</summary>
    /// <remarks>
    /// The distinction is the whole point of the column. A "have" category refuses the pick-up; an
    /// "equip" category lets you carry a bagful and refuses the second one onto your body.
    /// </remarks>
    public const uint ModeEquip = 1;
}

/// <summary>What kind of thing a skill line is. <c>SkillCategory</c>.</summary>
/// <remarks>
/// The category is what decides how a skill's bar behaves, which is not something the skill row
/// says in any more direct way. Armour and languages are the two that are not a bar at all.
/// </remarks>
public static class SkillCategory
{
    public const int Attributes = 5;
    public const int Weapon = 6;
    public const int Class = 7;
    public const int Armor = 8;

    /// <summary>Secondary professions — cooking, fishing, first aid.</summary>
    public const int Secondary = 9;

    public const int Languages = 10;

    /// <summary>Primary professions, of which a character may have two.</summary>
    public const int Profession = 11;

    public const int Generic = 12;
}

/// <summary>The skill ids referred to by name elsewhere. <c>SkillType</c>.</summary>
public static class SkillType
{
    public const uint Swords = 43;
    public const uint Axes = 44;
    public const uint Bows = 45;
    public const uint Guns = 46;
    public const uint Maces = 54;
    public const uint TwoHandedSwords = 55;
    public const uint Defense = 95;
    public const uint Staves = 136;
    public const uint TwoHandedMaces = 160;
    public const uint Unarmed = 162;
    public const uint TwoHandedAxes = 172;
    public const uint Daggers = 173;
    public const uint Thrown = 176;
    public const uint Crossbows = 226;
    public const uint Wands = 228;
    public const uint Polearms = 229;
    public const uint Assassination = 253;
    public const uint PlateMail = 293;
    public const uint Herbalism = 182;
    public const uint Mining = 186;
    public const uint Engineering = 202;
    public const uint Enchanting = 333;
    public const uint Fishing = 356;
    public const uint Skinning = 393;
    public const uint Mail = 413;
    public const uint Leather = 414;
    public const uint Cloth = 415;
    public const uint Shield = 433;
    public const uint FistWeapons = 473;
    public const uint Lockpicking = 633;
    public const uint Inscription = 773;
    public const uint Runeforging = 776;
    public const uint Mounts = 777;

    /// <summary>
    /// The skill a weapon subclass is swung with, indexed by subclass. <c>item_weapon_skills</c>.
    /// </summary>
    /// <remarks>
    /// The zeroes are real subclasses with no skill of their own — subclass 9 is the obsolete
    /// "exotic" pair and 11, 12 and 14 are bear, cat and miscellaneous. They have to stay in place:
    /// this is indexed by subclass, so removing them shifts every skill after them onto the wrong
    /// weapon.
    /// </remarks>
    private static readonly uint[] WeaponSkills =
    [
        Axes, TwoHandedAxes, Bows, Guns, Maces,
        TwoHandedMaces, Polearms, Swords, TwoHandedSwords, 0,
        Staves, 0, 0, FistWeapons, 0,
        Daggers, Thrown, Assassination, Crossbows, Wands,
        Fishing,
    ];

    /// <summary>The proficiency an armour subclass needs, indexed by subclass. <c>item_armor_skills</c>.</summary>
    private static readonly uint[] ArmorSkills =
        [0, Cloth, Leather, Mail, PlateMail, 0, Shield, 0, 0, 0, 0];

    /// <summary>
    /// The skill an item is used with, or 0 for something that needs none.
    /// </summary>
    /// <remarks>
    /// Port of <c>ItemTemplate::GetSkill</c>. Only weapons and armour have one — a subclass outside
    /// either table answers zero rather than throwing, since item data is content and a bad row
    /// should not take the server down.
    /// </remarks>
    public static uint ForItem(byte itemClass, byte subClass) => itemClass switch
    {
        DurabilityCostsEntry.ClassWeapon => subClass < WeaponSkills.Length ? WeaponSkills[subClass] : 0,
        DurabilityCostsEntry.ClassArmor => subClass < ArmorSkills.Length ? ArmorSkills[subClass] : 0,
        _ => 0,
    };
}

/// <summary>
/// How a skill's bar is scaled, which decides what its maximum means.
/// </summary>
/// <remarks>
/// Port of <c>SkillRangeType</c>. Not a column anywhere: it is derived from the skill's category
/// and whether it has a tier row, which is why <see cref="SkillLines.RangeOf"/> exists.
/// </remarks>
public enum SkillRange
{
    /// <summary>Flat 300, learned all at once. Common and Orcish are not practised.</summary>
    Language,

    /// <summary>1 up to five times the character's level — weapon and defence skills.</summary>
    Level,

    /// <summary>1..1, a grey monolithic bar. Armour proficiencies, which you either have or not.</summary>
    Mono,

    /// <summary>1 up to whatever the current tier allows — the professions.</summary>
    Rank,

    /// <summary>No bar at all, which is what an unknown skill gets.</summary>
    None,
}

/// <summary>A row of <c>SkillLine.dbc</c> — a skill, and what kind of thing it is.</summary>
public sealed record SkillLineEntry(uint Id, int CategoryId, string Name);

/// <summary>
/// A row of <c>SkillRaceClassInfo.dbc</c> — which races and classes may have a skill.
/// </summary>
/// <remarks>
/// The masks are what makes a skill available at all: a skill with no row matching a character's
/// race and class cannot be learned by them, which is how the client's own list stays honest.
/// </remarks>
public sealed record SkillRaceClassInfoEntry(
    uint Id, uint SkillId, uint RaceMask, uint ClassMask, uint Flags, uint SkillTierId)
{
    /// <summary>The skill starts at its maximum rather than at 1. <c>SKILL_FLAG_ALWAYS_MAX_VALUE</c>.</summary>
    public const uint AlwaysMaxValue = 0x10;

    /// <summary>Whether this row covers a given race and class.</summary>
    /// <remarks>
    /// A zero mask means "all", which is not the same as "none" — reading it literally would make
    /// every skill unavailable to everyone, since most rows leave both columns at zero.
    /// </remarks>
    public bool Covers(byte race, byte characterClass) =>
        (RaceMask == 0 || (RaceMask & (1u << (race - 1))) != 0)
        && (ClassMask == 0 || (ClassMask & (1u << (characterClass - 1))) != 0);
}

/// <summary>
/// A row of <c>SkillTiers.dbc</c> — the sixteen maximums a ranked skill steps through.
/// </summary>
public sealed record SkillTiersEntry(uint Id, uint[] Values)
{
    /// <summary>How many steps a ranked skill can have. <c>MAX_SKILL_STEP</c>.</summary>
    public const int MaxSteps = 16;

    /// <summary>The maximum at a given rank, which is 1-based — apprentice is rank 1.</summary>
    public uint MaxAt(ushort rank)
    {
        int index = Math.Max(rank - 1, 0);

        return index < Values.Length ? Values[index] : 0;
    }
}

/// <summary>
/// A row of <c>SkillLineAbility.dbc</c> — a spell, and the skill it belongs to.
/// </summary>
/// <remarks>
/// This is what connects the spellbook to the skill bar. Learning a spell whose row says
/// <see cref="LearnedOnSkillLearn"/> grants the skill itself, which is how a fresh warrior ends up
/// with Swords and Defense without any table saying so directly.
/// </remarks>
public sealed record SkillLineAbilityEntry(
    uint Id,
    uint SkillLine,
    uint Spell,
    uint RaceMask,
    uint ClassMask,
    uint MinSkillLineRank,
    uint SupercededBySpell,
    uint AcquireMethod,
    uint TrivialSkillLineRankHigh,
    uint TrivialSkillLineRankLow)
{
    /// <summary>The spell's availability tracks the skill's value. <c>..._ON_SKILL_VALUE</c>.</summary>
    public const uint LearnedOnSkillValue = 1;

    /// <summary>The spell comes and goes with the whole skill. <c>..._ON_SKILL_LEARN</c>.</summary>
    public const uint LearnedOnSkillLearn = 2;
}

/// <summary>A row of <c>LiquidType.dbc</c>.</summary>
/// <remarks>
/// Two columns of forty-five. <see cref="SoundBank"/> is what upstream calls <c>Type</c>, and it is
/// the only classification there is: it says whether a liquid is water, ocean, magma or slime, and
/// nothing else in the file does. A WMO stores only the row id, so without this table indoor water
/// and Undercity's slime are indistinguishable.
/// </remarks>
public sealed record LiquidTypeEntry(uint Id, uint SoundBank, uint SpellId)
{
    /// <summary>The <c>MAP_LIQUID_TYPE_*</c> bit this row's sound bank corresponds to.</summary>
    /// <remarks>
    /// The same mapping the map extractor makes when it bakes types into a terrain tile —
    /// <c>1 &lt;&lt; SoundBank</c>, spelled out rather than shifted so that an unexpected value
    /// becomes <see cref="LiquidTypeMask.None"/> instead of an out-of-range bit.
    /// </remarks>
    public LiquidTypeMask Type => SoundBank switch
    {
        0 => LiquidTypeMask.Water,
        1 => LiquidTypeMask.Ocean,
        2 => LiquidTypeMask.Magma,
        3 => LiquidTypeMask.Slime,
        _ => LiquidTypeMask.None,
    };
}

public sealed record WorldSafeLocsEntry(
    uint Id,
    uint MapId,
    float X,
    float Y,
    float Z,
    string Name);

/// <summary>A row of <c>ChrClasses.dbc</c>.</summary>
public sealed record ChrClassesEntry(
    uint ClassId,
    uint PowerType,
    string Name,
    uint SpellFamily,
    uint Expansion);

/// <summary>A row of <c>Map.dbc</c>.</summary>
public sealed record MapEntry(
    uint MapId,
    uint MapType,
    uint Flags,
    string Directory,
    string Name,
    uint LinkedZone,
    uint Expansion)
{
    /// <summary>Instances, raids, battlegrounds and arenas — anything that is not a continent.</summary>
    public bool IsInstance => MapType is 1 or 2 or 3 or 4;

    /// <summary>A world map: Azeroth, Kalimdor, Outland, Northrend.</summary>
    public bool IsContinent => MapType == 0;
}

/// <summary>
/// The DBC stores loaded at startup.
/// </summary>
/// <remarks>
/// Only the stores something reads are here. Upstream loads all 109 unconditionally, which costs
/// seconds of startup for tables no phase has touched yet; the rest arrive as they are needed.
/// <para>
/// The format strings are upstream's, verbatim from <c>DBCfmt.h</c>. They encode the exact column
/// layout of a 3.3.5a client's files — a single character out of place shifts every field after it
/// and produces values that are wrong but not obviously so.
/// </para>
/// </remarks>
public sealed class DbcStores
{
    // Verbatim from src/server/shared/DataStores/DBCfmt.h.
    private const string ChrRacesFormat = "niixiixixxxxiissssssssssssssssxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxi";
    private const string ChrClassesFormat = "nxixssssssssssssssssxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxixii";
    private const string MapFormat = "nxiixssssssssssssssssxixxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxixiffxixi";
    private const string FactionTemplateFormat = "niiiiiiiiiiiii";

    // Not in upstream's DBCfmt.h — the C++ stopped reading this file when graveyards moved into
    // the world database. Derived from the file itself: 22 fields, id + map + three floats, then
    // sixteen locale names and a flags word.
    private const string WorldSafeLocsFormat = "nifffssssssssssssssssx";

    /// <summary>An id and ten difficulty columns. <c>QuestXPfmt</c>.</summary>
    private const string QuestXpFormat = "niiiiiiiiii";

    /// <summary>Verbatim from <c>DBCfmt.h</c>: id, type and spell out of forty-five columns.</summary>
    private const string LiquidTypeFormat = "nxxixixxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    /// <summary>
    /// Verbatim from <c>DBCfmt.h</c>. Thirty-six columns, sixteen of them the localised name.
    /// </summary>
    private const string AreaTableFormat = "niiiixxxxxissssssssssssssssxiiiiixxx";

    /// <summary>An item level and twenty-nine multipliers. <c>DurabilityCostsfmt</c>.</summary>
    private const string DurabilityCostsFormat = "niiiiiiiiiiiiiiiiiiiiiiiiiiiii";

    /// <summary>An id and one float. <c>DurabilityQualityfmt</c>.</summary>
    private const string DurabilityQualityFormat = "nf";

    /// <summary>A single bare float. The <c>gt*</c> tables have no id column at all.</summary>
    private const string GameTableFloatFormat = "f";

    /// <summary>An id and a float — the one <c>gt*</c> table that does carry an id.</summary>
    private const string GameTableScalarFormat = "nf";

    /// <summary>
    /// Thirty-eight columns. The three <c>x</c> runs skip the maximum amounts (which 3.3.5 does not
    /// use) and the name flags. <c>SpellItemEnchantmentfmt</c>.
    /// </summary>
    private const string SpellItemEnchantmentFormat =
        "niiiiiiixxxiiissssssssssssssssxiiiiiii";

    /// <summary>An id, a skipped internal name, five enchantments and sixteen names. <c>ItemRandomPropertiesfmt</c>.</summary>
    private const string ItemRandomPropertiesFormat = "nxiiiiissssssssssssssssx";

    /// <summary>Names first here, then the enchantments and their allocations. <c>ItemRandomSuffixfmt</c>.</summary>
    private const string ItemRandomSuffixFormat = "nssssssssssssssssxxiiiiiiiiii";

    /// <summary>
    /// Sixteen columns, keyed by item level. <c>RandomPropertiesPointsfmt</c>.
    /// </summary>
    /// <remarks>
    /// The struct's comments in <c>DBCStructure.h</c> count a hidden key that is not there, so its
    /// indices run one high. The format string is what defines the offsets.
    /// </remarks>
    private const string RandomPropertyPointsFormat = "niiiiiiiiiiiiiii";

    /// <summary>
    /// Thirty-seven columns: an id, two blocks of sixteen names, and the bit index last.
    /// <c>CharTitlesEntryfmt</c>.
    /// </summary>
    private const string CharTitlesFormat = "nxssssssssssssssssxssssssssssssssssxi";

    /// <summary>An id and ten amounts. <c>QuestFactionRewardfmt</c>.</summary>
    private const string QuestFactionRewardFormat = "niiiiiiiiii";

    /// <summary>Fifty-seven columns, most of them localised text. <c>FactionEntryfmt</c>.</summary>
    private const string FactionFormat =
        "niiiiiiiiiiiiiiiiiiffixssssssssssssssssxxxxxxxxxxxxxxxxxx";

    /// <summary>Thirty-three columns; the last eight are the actions we do not use. <c>LockEntryfmt</c>.</summary>
    private const string LockFormat = "niiiiiiiiiiiiiiiiiiiiiiiixxxxxxxx";

    /// <summary>A slot number and a price. <c>BankBagSlotPricesEntryfmt</c>.</summary>
    private const string BankBagSlotPricesFormat = "ni";

    /// <summary>Twenty columns, seventeen of them skipped text. <c>ItemLimitCategoryEntryfmt</c>.</summary>
    private const string ItemLimitCategoryFormat = "nxxxxxxxxxxxxxxxxxii";

    /// <summary>Fifty-six columns, most of them localised text. <c>SkillLinefmt</c>.</summary>
    private const string SkillLineFormat =
        "nixssssssssssssssssxxxxxxxxxxxxxxxxxxixxxxxxxxxxxxxxxxxi";

    /// <summary>
    /// <c>SkillRaceClassInfofmt</c>. The leading <c>d</c> is the row id, which is present in the
    /// file but not part of upstream's struct — we keep it, since it is what indexes the store.
    /// </summary>
    private const string SkillRaceClassInfoFormat = "diiiixix";

    /// <summary>An id, sixteen costs we skip, then sixteen maximums. <c>SkillTiersfmt</c>.</summary>
    private const string SkillTiersFormat = "nxxxxxxxxxxxxxxxxiiiiiiiiiiiiiiii";

    /// <summary>Fourteen columns. <c>SkillLineAbilityfmt</c>.</summary>
    private const string SkillLineAbilityFormat = "niiiixxiiiiixx";

    private DbcStores(
        DbcStore<ChrRacesEntry> races,
        DbcStore<ChrClassesEntry> classes,
        DbcStore<MapEntry> maps,
        DbcStore<FactionTemplateEntry> factionTemplates,
        DbcStore<WorldSafeLocsEntry> worldSafeLocs,
        DbcStore<QuestXpEntry> questXp,
        DbcStore<LiquidTypeEntry> liquidTypes,
        DbcStore<AreaTableEntry> areas,
        DbcStore<DurabilityCostsEntry> durabilityCosts,
        DbcStore<DurabilityQualityEntry> durabilityQuality,
        SkillLines skills,
        DbcStore<ItemLimitCategoryEntry> itemLimitCategories,
        DbcStore<BankBagSlotPriceEntry> bankBagSlotPrices,
        CombatRatingTable combatRatings,
        AttributeChanceTable attributeChances,
        DbcStore<LockEntry> locks,
        DbcStore<FactionEntry> factions,
        DbcStore<QuestFactionRewardEntry> questFactionRewards,
        DbcStore<CharTitleEntry> charTitles,
        DbcStore<SpellItemEnchantmentEntry> itemEnchantments,
        DbcStore<ItemRandomPropertiesEntry> itemRandomProperties,
        DbcStore<ItemRandomSuffixEntry> itemRandomSuffixes,
        DbcStore<RandomPropertyPointsEntry> randomPropertyPoints)
    {
        Locks = locks;
        Factions = factions;
        QuestFactionRewards = questFactionRewards;
        CharTitles = charTitles;
        ItemEnchantments = itemEnchantments;
        ItemRandomProperties = itemRandomProperties;
        ItemRandomSuffixes = itemRandomSuffixes;
        RandomPropertyPoints = randomPropertyPoints;
        CombatRatings = combatRatings;
        AttributeChances = attributeChances;
        Skills = skills;
        ItemLimitCategories = itemLimitCategories;
        BankBagSlotPrices = bankBagSlotPrices;
        DurabilityCosts = durabilityCosts;
        DurabilityQuality = durabilityQuality;
        QuestXp = questXp;
        LiquidTypes = liquidTypes;
        Areas = areas;
        Races = races;
        Classes = classes;
        Maps = maps;
        FactionTemplates = factionTemplates;
        WorldSafeLocs = worldSafeLocs;
    }

    public DbcStore<ChrRacesEntry> Races { get; }

    public DbcStore<ChrClassesEntry> Classes { get; }

    public DbcStore<MapEntry> Maps { get; }

    /// <summary>Who fights whom.</summary>
    public DbcStore<FactionTemplateEntry> FactionTemplates { get; }

    /// <summary>Graveyards and other named safe points.</summary>
    public DbcStore<WorldSafeLocsEntry> WorldSafeLocs { get; }

    /// <summary>
    /// How much experience a quest pays, by quest level and difficulty.
    /// </summary>
    /// <remarks>
    /// <b>Indexed by the quest's LEVEL, not by its id.</b> The row is the level and the column is
    /// the quest's <c>RewardXPId</c>, which is a difficulty band rather than an amount.
    /// </remarks>
    public DbcStore<QuestXpEntry> QuestXp { get; }

    /// <summary>What each liquid actually is. Without it a WMO's water has no type at all.</summary>
    public DbcStore<LiquidTypeEntry> LiquidTypes { get; }

    /// <summary>
    /// Zones and subzones. What turns the area id a terrain tile stores into a zone.
    /// </summary>
    /// <remarks>
    /// The distinction matters more than it looks. A terrain chunk stores the <i>area</i>, and
    /// everything keyed by zone — graveyards, the character list, the location display — wants the
    /// zone. Using one for the other works everywhere a zone has no subzones and fails silently
    /// everywhere it does.
    /// </remarks>
    public DbcStore<AreaTableEntry> Areas { get; }

    /// <summary>What a point of durability costs, by item level and kind.</summary>
    public DbcStore<DurabilityCostsEntry> DurabilityCosts { get; }

    /// <summary>How an item's quality scales that cost.</summary>
    public DbcStore<DurabilityQualityEntry> DurabilityQuality { get; }

    /// <summary>The skill tables, and the cross-table lookups worth having.</summary>
    public SkillLines Skills { get; }

    /// <summary>How many of a family of items may be held or worn.</summary>
    public DbcStore<ItemLimitCategoryEntry> ItemLimitCategories { get; }

    /// <summary>What the next bank bag slot costs.</summary>
    public DbcStore<BankBagSlotPriceEntry> BankBagSlotPrices { get; }

    /// <summary>What a point of a combat rating is worth, by level and class.</summary>
    public CombatRatingTable CombatRatings { get; }

    /// <summary>What agility and intellect are worth, before any gear.</summary>
    public AttributeChanceTable AttributeChances { get; }

    /// <summary>What it takes to open a locked thing.</summary>
    public DbcStore<LockEntry> Locks { get; }

    /// <summary>Every faction a character can have standing with.</summary>
    public DbcStore<FactionEntry> Factions { get; }

    /// <summary>The ten reputation amounts a quest can pay, in gains and losses.</summary>
    public DbcStore<QuestFactionRewardEntry> QuestFactionRewards { get; }

    /// <summary>Every title in the game, and where each sits in the known-titles mask.</summary>
    public DbcStore<CharTitleEntry> CharTitles { get; }

    /// <summary>Every enchantment an item can carry. <c>SpellItemEnchantment.dbc</c>.</summary>
    public DbcStore<SpellItemEnchantmentEntry> ItemEnchantments { get; }

    /// <summary>The fixed-amount random suffixes. Reached by a POSITIVE RandomProperty id.</summary>
    public DbcStore<ItemRandomPropertiesEntry> ItemRandomProperties { get; }

    /// <summary>The scaled random suffixes. Reached by a NEGATIVE one, by its absolute value.</summary>
    public DbcStore<ItemRandomSuffixEntry> ItemRandomSuffixes { get; }

    /// <summary>How many stat points a random suffix is worth, by item level.</summary>
    public DbcStore<RandomPropertyPointsEntry> RandomPropertyPoints { get; }

    /// <summary>
    /// The zone an area belongs to, which is the area itself when it is already a zone.
    /// </summary>
    /// <remarks>
    /// Falls back to the area id when the row is missing rather than answering zero: an unknown area
    /// is better treated as its own zone than as no zone at all, which would silently disable
    /// everything keyed by one.
    /// </remarks>
    public uint ZoneFor(uint areaId) =>
        Areas.TryGet(areaId, out AreaTableEntry? area) && area is not null && area.ParentZoneId != 0
            ? area.ParentZoneId
            : areaId;

    /// <summary>Total rows loaded, for the startup log.</summary>
    public int TotalRows =>
        Races.Count + Classes.Count + Maps.Count + FactionTemplates.Count + WorldSafeLocs.Count
        + QuestXp.Count + LiquidTypes.Count + Areas.Count
        + DurabilityCosts.Count + DurabilityQuality.Count + Skills.TotalRows
        + ItemLimitCategories.Count + BankBagSlotPrices.Count;

    /// <summary>
    /// Loads every store from a directory of extracted <c>.dbc</c> files.
    /// </summary>
    /// <param name="directory">Usually <c>data/dbc</c>.</param>
    /// <param name="locale">Preferred locale slot, 0-15. Ignored when a store has one locale filled.</param>
    public static DbcStores Load(string directory, int locale = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"No DBC directory at '{directory}'. Extract a 3.3.5a client into data/dbc — see data/README.md.");
        }

        return new DbcStores(
            DbcStore<ChrRacesEntry>.Load(
                Path.Combine(directory, "ChrRaces.dbc"),
                ChrRacesFormat,
                idField: 0,
                (in DbcRecord record) => new ChrRacesEntry(
                    RaceId: record.GetUInt32(0),
                    Flags: record.GetUInt32(1),
                    FactionId: record.GetUInt32(2),
                    MaleDisplayId: record.GetUInt32(4),
                    FemaleDisplayId: record.GetUInt32(5),
                    TeamId: record.GetUInt32(7),
                    CinematicSequenceId: record.GetUInt32(12),
                    Alliance: record.GetUInt32(13),
                    Name: record.GetLocalizedString(14, locale),
                    Expansion: record.GetUInt32(68))),

            DbcStore<ChrClassesEntry>.Load(
                Path.Combine(directory, "ChrClasses.dbc"),
                ChrClassesFormat,
                idField: 0,
                (in DbcRecord record) => new ChrClassesEntry(
                    ClassId: record.GetUInt32(0),
                    PowerType: record.GetUInt32(2),
                    // Column 4, not 5: the struct's comment in DBCStructure.h is off by one and
                    // the format string is what actually defines the byte offsets.
                    Name: record.GetLocalizedString(4, locale),
                    SpellFamily: record.GetUInt32(56),
                    Expansion: record.GetUInt32(59))),

            DbcStore<MapEntry>.Load(
                Path.Combine(directory, "Map.dbc"),
                MapFormat,
                idField: 0,
                (in DbcRecord record) => new MapEntry(
                    MapId: record.GetUInt32(0),
                    MapType: record.GetUInt32(2),
                    Flags: record.GetUInt32(3),
                    // Column 1 is the map's folder name. Upstream's format marks it unused, but it
                    // is a perfectly good string offset and it is what data/maps subdirectories are
                    // named after, so it is read here.
                    Directory: record.GetString(1),
                    Name: record.GetLocalizedString(5, locale),
                    LinkedZone: record.GetUInt32(22),
                    Expansion: record.GetUInt32(63))),

            DbcStore<FactionTemplateEntry>.Load(
                Path.Combine(directory, "FactionTemplate.dbc"),
                FactionTemplateFormat,
                idField: 0,
                (in DbcRecord record) => new FactionTemplateEntry(
                    Id: record.GetUInt32(0),
                    Faction: record.GetUInt32(1),
                    Flags: record.GetUInt32(2),
                    OurMask: record.GetUInt32(3),
                    FriendlyMask: record.GetUInt32(4),
                    HostileMask: record.GetUInt32(5),
                    // Four each, consecutive: enemies at 6-9, friends at 10-13. Reading them as one
                    // block of eight would compile and would make every friend an enemy.
                    EnemyFactions:
                    [
                        record.GetUInt32(6),
                        record.GetUInt32(7),
                        record.GetUInt32(8),
                        record.GetUInt32(9),
                    ],
                    FriendFactions:
                    [
                        record.GetUInt32(10),
                        record.GetUInt32(11),
                        record.GetUInt32(12),
                        record.GetUInt32(13),
                    ])),

            DbcStore<WorldSafeLocsEntry>.Load(
                Path.Combine(directory, "WorldSafeLocs.dbc"),
                WorldSafeLocsFormat,
                idField: 0,
                (in DbcRecord record) => new WorldSafeLocsEntry(
                    Id: record.GetUInt32(0),
                    MapId: record.GetUInt32(1),
                    X: record.GetFloat(2),
                    Y: record.GetFloat(3),
                    Z: record.GetFloat(4),
                    Name: record.GetLocalizedString(5, locale))),

            DbcStore<QuestXpEntry>.Load(
                Path.Combine(directory, "QuestXP.dbc"),
                QuestXpFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] byDifficulty = new uint[QuestXpEntry.DifficultyCount];

                    for (int i = 0; i < byDifficulty.Length; i++)
                    {
                        byDifficulty[i] = record.GetUInt32(1 + i);
                    }

                    return new QuestXpEntry(record.GetUInt32(0), byDifficulty);
                }),

            DbcStore<LiquidTypeEntry>.Load(
                Path.Combine(directory, "LiquidType.dbc"),
                LiquidTypeFormat,
                idField: 0,
                // Columns, not kept fields: an 'x' in the format still consumes an index, so the
                // type sits at 3 and the spell at 5 even though they are the second and third
                // things the format keeps. Reading them at 1 and 2 lands on the name's string
                // offset, which resolves to a plausible small number and quietly types every
                // liquid as nothing.
                (in DbcRecord record) => new LiquidTypeEntry(
                    Id: record.GetUInt32(0),
                    SoundBank: record.GetUInt32(3),
                    SpellId: record.GetUInt32(5))),

            DbcStore<AreaTableEntry>.Load(
                Path.Combine(directory, "AreaTable.dbc"),
                AreaTableFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] overrides = new uint[AreaTableEntry.LiquidOverrideCount];

                    for (int i = 0; i < overrides.Length; i++)
                    {
                        overrides[i] = record.GetUInt32(29 + i);
                    }

                    return new AreaTableEntry(
                        Id: record.GetUInt32(0),
                        MapId: record.GetUInt32(1),
                        ParentZoneId: record.GetUInt32(2),
                        Flags: record.GetUInt32(4),
                        AreaLevel: record.GetInt32(10),
                        Name: record.GetLocalizedString(11, locale),
                        Team: record.GetUInt32(28),
                        LiquidTypeOverride: overrides);
                }),

            DbcStore<DurabilityCostsEntry>.Load(
                Path.Combine(directory, "DurabilityCosts.dbc"),
                DurabilityCostsFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] multipliers = new uint[DurabilityCostsEntry.MultiplierCount];

                    for (int i = 0; i < multipliers.Length; i++)
                    {
                        multipliers[i] = record.GetUInt32(1 + i);
                    }

                    return new DurabilityCostsEntry(record.GetUInt32(0), multipliers);
                }),

            DbcStore<DurabilityQualityEntry>.Load(
                Path.Combine(directory, "DurabilityQuality.dbc"),
                DurabilityQualityFormat,
                idField: 0,
                (in DbcRecord record) => new DurabilityQualityEntry(
                    record.GetUInt32(0), record.GetFloat(1))),

            LoadSkills(directory, locale),

            DbcStore<ItemLimitCategoryEntry>.Load(
                Path.Combine(directory, "ItemLimitCategory.dbc"),
                ItemLimitCategoryFormat,
                idField: 0,
                (in DbcRecord record) => new ItemLimitCategoryEntry(
                    Id: record.GetUInt32(0),
                    MaxCount: record.GetUInt32(18),
                    Mode: record.GetUInt32(19))),

            DbcStore<BankBagSlotPriceEntry>.Load(
                Path.Combine(directory, "BankBagSlotPrices.dbc"),
                BankBagSlotPricesFormat,
                idField: 0,
                (in DbcRecord record) => new BankBagSlotPriceEntry(
                    Slot: record.GetUInt32(0),
                    Price: record.GetUInt32(1))),

            new CombatRatingTable(
                DbcStore<GameTableFloat>.LoadByOrdinal(
                    Path.Combine(directory, "gtCombatRatings.dbc"),
                    GameTableFloatFormat,
                    (in DbcRecord record) => new GameTableFloat(record.GetFloat(0))),

                DbcStore<GameTableScalar>.Load(
                    Path.Combine(directory, "gtOCTClassCombatRatingScalar.dbc"),
                    GameTableScalarFormat,
                    idField: 0,
                    (in DbcRecord record) => new GameTableScalar(
                        record.GetUInt32(0), record.GetFloat(1)))),

            new AttributeChanceTable(
                GameTable(directory, "gtChanceToMeleeCritBase.dbc"),
                GameTable(directory, "gtChanceToMeleeCrit.dbc"),
                GameTable(directory, "gtChanceToSpellCritBase.dbc"),
                GameTable(directory, "gtChanceToSpellCrit.dbc")),

            DbcStore<LockEntry>.Load(
                Path.Combine(directory, "Lock.dbc"),
                LockFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] types = new uint[LockEntry.Cases];
                    uint[] indices = new uint[LockEntry.Cases];
                    uint[] skills = new uint[LockEntry.Cases];

                    for (int i = 0; i < LockEntry.Cases; i++)
                    {
                        types[i] = record.GetUInt32(1 + i);
                        indices[i] = record.GetUInt32(9 + i);
                        skills[i] = record.GetUInt32(17 + i);
                    }

                    return new LockEntry(record.GetUInt32(0), types, indices, skills);
                }),

            DbcStore<FactionEntry>.Load(
                Path.Combine(directory, "Faction.dbc"),
                FactionFormat,
                idField: 0,
                (in DbcRecord record) => new FactionEntry(
                    Id: record.GetUInt32(0),
                    ReputationListId: record.GetInt32(1),
                    ParentFactionId: record.GetUInt32(18),
                    Name: record.GetLocalizedString(23, locale))),

            DbcStore<QuestFactionRewardEntry>.Load(
                Path.Combine(directory, "QuestFactionReward.dbc"),
                QuestFactionRewardFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    int[] values = new int[QuestFactionRewardEntry.Count];

                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = record.GetInt32(1 + i);
                    }

                    return new QuestFactionRewardEntry(record.GetUInt32(0), values);
                }),

            DbcStore<CharTitleEntry>.Load(
                Path.Combine(directory, "CharTitles.dbc"),
                CharTitlesFormat,
                idField: 0,
                (in DbcRecord record) => new CharTitleEntry(
                    Id: record.GetUInt32(0),
                    // Last column, after both name blocks and their string flags.
                    BitIndex: record.GetUInt32(36),
                    Name: record.GetLocalizedString(2, locale))),

            DbcStore<SpellItemEnchantmentEntry>.Load(
                Path.Combine(directory, "SpellItemEnchantment.dbc"),
                SpellItemEnchantmentFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] types = new uint[SpellItemEnchantmentEntry.Effects];
                    int[] amounts = new int[SpellItemEnchantmentEntry.Effects];
                    uint[] spellIds = new uint[SpellItemEnchantmentEntry.Effects];

                    for (int i = 0; i < SpellItemEnchantmentEntry.Effects; i++)
                    {
                        types[i] = record.GetUInt32(2 + i);
                        amounts[i] = record.GetInt32(5 + i);

                        // 11, not 8: columns 8 to 10 are the maximum amounts, which 3.3.5 never
                        // uses and the format string skips.
                        spellIds[i] = record.GetUInt32(11 + i);
                    }

                    return new SpellItemEnchantmentEntry(
                        Id: record.GetUInt32(0),
                        Charges: record.GetUInt32(1),
                        Types: types,
                        Amounts: amounts,
                        SpellIds: spellIds,
                        AuraId: record.GetUInt32(31),
                        Slot: record.GetUInt32(32),
                        GemItemId: record.GetUInt32(33),
                        RequiredSkill: record.GetUInt32(35),
                        RequiredSkillValue: record.GetUInt32(36),
                        RequiredLevel: record.GetUInt32(37),
                        Name: record.GetLocalizedString(14, locale));
                }),

            DbcStore<ItemRandomPropertiesEntry>.Load(
                Path.Combine(directory, "ItemRandomProperties.dbc"),
                ItemRandomPropertiesFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] enchantments = new uint[ItemRandomPropertiesEntry.Enchants];

                    for (int i = 0; i < enchantments.Length; i++)
                    {
                        enchantments[i] = record.GetUInt32(2 + i);
                    }

                    return new ItemRandomPropertiesEntry(
                        record.GetUInt32(0), enchantments, record.GetLocalizedString(7, locale));
                }),

            DbcStore<ItemRandomSuffixEntry>.Load(
                Path.Combine(directory, "ItemRandomSuffix.dbc"),
                ItemRandomSuffixFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] enchantments = new uint[ItemRandomSuffixEntry.Enchants];
                    uint[] allocations = new uint[ItemRandomSuffixEntry.Enchants];

                    for (int i = 0; i < enchantments.Length; i++)
                    {
                        enchantments[i] = record.GetUInt32(19 + i);
                        allocations[i] = record.GetUInt32(24 + i);
                    }

                    return new ItemRandomSuffixEntry(
                        record.GetUInt32(0), enchantments, allocations,
                        record.GetLocalizedString(1, locale));
                }),

            DbcStore<RandomPropertyPointsEntry>.Load(
                Path.Combine(directory, "RandPropPoints.dbc"),
                RandomPropertyPointsFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] epic = new uint[RandomPropertyPointsEntry.Coefficients];
                    uint[] rare = new uint[RandomPropertyPointsEntry.Coefficients];
                    uint[] uncommon = new uint[RandomPropertyPointsEntry.Coefficients];

                    for (int i = 0; i < RandomPropertyPointsEntry.Coefficients; i++)
                    {
                        epic[i] = record.GetUInt32(1 + i);
                        rare[i] = record.GetUInt32(6 + i);
                        uncommon[i] = record.GetUInt32(11 + i);
                    }

                    return new RandomPropertyPointsEntry(record.GetUInt32(0), epic, rare, uncommon);
                }));
    }

    /// <summary>One of the bare-float <c>gt*</c> tables, keyed by row position.</summary>
    private static DbcStore<GameTableFloat> GameTable(string directory, string file) =>
        DbcStore<GameTableFloat>.LoadByOrdinal(
            Path.Combine(directory, file),
            GameTableFloatFormat,
            (in DbcRecord record) => new GameTableFloat(record.GetFloat(0)));

    /// <summary>The four skill tables, loaded together because none of them is useful alone.</summary>
    private static SkillLines LoadSkills(string directory, int locale)
    {
        DbcStore<SkillLineEntry> lines = DbcStore<SkillLineEntry>.Load(
            Path.Combine(directory, "SkillLine.dbc"),
            SkillLineFormat,
            idField: 0,
            (in DbcRecord record) => new SkillLineEntry(
                Id: record.GetUInt32(0),
                CategoryId: record.GetInt32(1),
                Name: record.GetLocalizedString(3, locale)));

        DbcStore<SkillRaceClassInfoEntry> raceClassInfo = DbcStore<SkillRaceClassInfoEntry>.Load(
            Path.Combine(directory, "SkillRaceClassInfo.dbc"),
            SkillRaceClassInfoFormat,
            idField: 0,
            (in DbcRecord record) => new SkillRaceClassInfoEntry(
                Id: record.GetUInt32(0),
                SkillId: record.GetUInt32(1),
                RaceMask: record.GetUInt32(2),
                ClassMask: record.GetUInt32(3),
                Flags: record.GetUInt32(4),
                SkillTierId: record.GetUInt32(6)));

        DbcStore<SkillTiersEntry> tiers = DbcStore<SkillTiersEntry>.Load(
            Path.Combine(directory, "SkillTiers.dbc"),
            SkillTiersFormat,
            idField: 0,
            (in DbcRecord record) =>
            {
                uint[] values = new uint[SkillTiersEntry.MaxSteps];

                for (int step = 0; step < values.Length; step++)
                {
                    // The sixteen costs sit between the id and the maximums and are skipped in the
                    // format string, but they still occupy columns — the values start at 17.
                    values[step] = record.GetUInt32(17 + step);
                }

                return new SkillTiersEntry(record.GetUInt32(0), values);
            });

        DbcStore<SkillLineAbilityEntry> abilities = DbcStore<SkillLineAbilityEntry>.Load(
            Path.Combine(directory, "SkillLineAbility.dbc"),
            SkillLineAbilityFormat,
            idField: 0,
            (in DbcRecord record) => new SkillLineAbilityEntry(
                Id: record.GetUInt32(0),
                SkillLine: record.GetUInt32(1),
                Spell: record.GetUInt32(2),
                RaceMask: record.GetUInt32(3),
                ClassMask: record.GetUInt32(4),
                MinSkillLineRank: record.GetUInt32(7),
                SupercededBySpell: record.GetUInt32(8),
                AcquireMethod: record.GetUInt32(9),
                TrivialSkillLineRankHigh: record.GetUInt32(10),
                TrivialSkillLineRankLow: record.GetUInt32(11)));

        return new SkillLines(lines, raceClassInfo, tiers, abilities)
        {
            TotalRows = lines.Count + raceClassInfo.Count + tiers.Count + abilities.Count,
        };
    }
}
