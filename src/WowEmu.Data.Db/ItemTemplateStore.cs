using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>How many of each repeating block an item row carries.</summary>
/// <remarks>
/// Fixed by the client's own tooltip layout, not by the table: the query response writes exactly
/// this many of each, and a mismatch shifts every field after it.
/// </remarks>
public static class ItemConstants
{
    /// <summary>Stat pairs. <c>MAX_ITEM_PROTO_STATS</c>.</summary>
    public const int MaxStats = 10;

    /// <summary>Damage ranges. <b>Two, not five</b> — 3.1.0 cut it down. <c>MAX_ITEM_PROTO_DAMAGES</c>.</summary>
    public const int MaxDamages = 2;

    /// <summary>Gem sockets. <c>MAX_ITEM_PROTO_SOCKETS</c>.</summary>
    public const int MaxSockets = 3;

    /// <summary>On-use and on-equip spells. <c>MAX_ITEM_PROTO_SPELLS</c>.</summary>
    public const int MaxSpells = 5;
}

/// <summary>Item classes. <c>ItemClass</c>.</summary>
public static class ItemClass
{
    public const byte Consumable = 0;
    public const byte Container = 1;
    public const byte Weapon = 2;
    public const byte Gem = 3;
    public const byte Armor = 4;
    public const byte Reagent = 5;
    public const byte Projectile = 6;
    public const byte TradeGoods = 7;
    public const byte Generic = 8;
    public const byte Recipe = 9;
    public const byte Money = 10;
    public const byte Quiver = 11;
    public const byte Quest = 12;
    public const byte Key = 13;
    public const byte Permanent = 14;
    public const byte Misc = 15;
    public const byte Glyph = 16;
}

/// <summary>Item qualities, which are also the tooltip's colours. <c>ItemQualities</c>.</summary>
/// <summary>
/// When an item becomes the holder's for good. <c>ItemBondingType</c>.
/// </summary>
/// <remarks>
/// The distinction between <see cref="OnPickup"/> and <see cref="OnEquip"/> is the whole of item
/// trading: a bind-on-equip item is worth something on the market until the moment somebody wears
/// it, and binding it on pickup instead destroys that entire economy quietly.
/// </remarks>
public static class ItemBonding
{
    public const byte None = 0;
    public const byte OnPickup = 1;
    public const byte OnEquip = 2;
    public const byte OnUse = 3;
    public const byte QuestItem = 4;

    /// <summary>Present in the column and never used by the game data.</summary>
    public const byte QuestItemUnused = 5;
}

public static class ItemQuality
{
    public const byte Poor = 0;
    public const byte Normal = 1;
    public const byte Uncommon = 2;
    public const byte Rare = 3;
    public const byte Epic = 4;
    public const byte Legendary = 5;
    public const byte Artifact = 6;
    public const byte Heirloom = 7;
}

/// <summary>
/// Where an item goes when equipped. <c>InventoryType</c>.
/// </summary>
/// <remarks>
/// <b>Not the same numbering as the equipment slots it maps to.</b> One inventory type can fit
/// several slots — a ring is <see cref="Finger"/> and goes in either of two — and the mapping is a
/// table rather than an offset.
/// </remarks>
public static class InventoryType
{
    public const byte NonEquip = 0;
    public const byte Head = 1;
    public const byte Neck = 2;
    public const byte Shoulders = 3;
    public const byte Body = 4;
    public const byte Chest = 5;
    public const byte Waist = 6;
    public const byte Legs = 7;
    public const byte Feet = 8;
    public const byte Wrists = 9;
    public const byte Hands = 10;
    public const byte Finger = 11;
    public const byte Trinket = 12;
    public const byte Weapon = 13;
    public const byte Shield = 14;
    public const byte Ranged = 15;
    public const byte Cloak = 16;
    public const byte TwoHandWeapon = 17;
    public const byte Bag = 18;
    public const byte Tabard = 19;
    public const byte Robe = 20;
    public const byte WeaponMainHand = 21;
    public const byte WeaponOffHand = 22;
    public const byte Holdable = 23;
    public const byte Ammo = 24;
    public const byte Thrown = 25;
    public const byte RangedRight = 26;
    public const byte Quiver = 27;
    public const byte Relic = 28;
}

/// <summary>One of an item's ten stat lines.</summary>
/// <param name="Type">An <c>ItemModType</c> — strength, stamina, crit rating, and so on.</param>
public readonly record struct ItemStat(byte Type, short Value);

/// <summary>One of a weapon's two damage ranges.</summary>
/// <param name="School">A spell school, so a weapon can deal something other than physical.</param>
public readonly record struct ItemDamage(float Min, float Max, byte School);

/// <summary>One of an item's five spells.</summary>
/// <param name="Trigger">
/// What sets it off: 0 on use, 1 on equip, 2 on hit, and so on. It is what separates an on-equip
/// bonus from a clickable.
/// </param>
/// <param name="Charges">
/// Uses before the spell stops working. <b>Negative means the item is destroyed</b> when they run
/// out, which is how a potion disappears; positive charges leave the item behind.
/// </param>
/// <param name="CooldownMs">
/// The item's own cooldown, or <c>-1</c> to fall back to the spell's. Both this and
/// <paramref name="CategoryCooldownMs"/> being negative is what tells the query response to send
/// the spell's figures instead of the table's.
/// </param>
public readonly record struct ItemSpell(
    int SpellId,
    byte Trigger,
    short Charges,
    int CooldownMs,
    ushort Category,
    int CategoryCooldownMs)
{
    /// <summary>Whether the table has an opinion about the cooldown, or the spell should be asked.</summary>
    public bool HasCooldownData => CooldownMs >= 0 || CategoryCooldownMs >= 0;
}

/// <summary>One of an item's three gem sockets.</summary>
public readonly record struct ItemSocket(sbyte Color, int Content);

/// <summary>
/// One row of <c>item_template</c>.
/// </summary>
/// <remarks>
/// Port of <c>ItemTemplate</c>. Wide where the other template records are narrow, and deliberately:
/// nearly every column here is written straight back out in
/// <c>SMSG_ITEM_QUERY_SINGLE_RESPONSE</c>, which is how the client draws a tooltip it has never
/// seen. Reading a subset would mean an item whose tooltip is missing whatever was left out.
/// <para>
/// The seven columns that are <i>not</i> here — <c>ScriptName</c>, <c>DisenchantID</c>,
/// <c>FoodType</c>, the two money-loot columns, <c>flagsCustom</c> and <c>VerifiedBuild</c> — are
/// server-side and never reach the client.
/// </para>
/// </remarks>
public sealed record ItemTemplate(
    uint Entry,
    byte Class,
    byte SubClass,
    sbyte SoundOverrideSubclass,
    string Name,
    uint DisplayId,
    byte Quality,
    uint Flags,
    uint FlagsExtra,
    byte BuyCount,
    long BuyPrice,
    uint SellPrice,
    byte InventoryType,
    int AllowableClass,
    int AllowableRace,
    ushort ItemLevel,
    byte RequiredLevel,
    ushort RequiredSkill,
    ushort RequiredSkillRank,
    uint RequiredSpell,
    uint RequiredHonorRank,
    uint RequiredCityRank,
    ushort RequiredReputationFaction,
    ushort RequiredReputationRank,
    int MaxCount,
    int Stackable,
    byte ContainerSlots,
    byte StatsCount,
    ItemStat[] Stats,
    short ScalingStatDistribution,
    uint ScalingStatValue,
    ItemDamage[] Damage,
    ushort Armor,
    ushort HolyResistance,
    ushort FireResistance,
    ushort NatureResistance,
    ushort FrostResistance,
    ushort ShadowResistance,
    ushort ArcaneResistance,
    ushort Delay,
    byte AmmoType,
    float RangedModRange,
    ItemSpell[] Spells,
    byte Bonding,
    string Description,
    uint PageText,
    byte LanguageId,
    byte PageMaterial,
    uint StartQuest,
    uint LockId,
    sbyte Material,
    byte Sheath,
    int RandomProperty,
    uint RandomSuffix,
    uint Block,
    uint ItemSet,
    ushort MaxDurability,
    uint Area,
    short Map,
    int BagFamily,
    int TotemCategory,
    ItemSocket[] Sockets,
    int SocketBonus,
    int GemProperties,
    short RequiredDisenchantSkill,
    float ArmorDamageModifier,
    uint DurationSeconds,
    short ItemLimitCategory,
    uint HolidayId)
{
    /// <summary>
    /// The largest stack the client will let this item form.
    /// </summary>
    /// <remarks>
    /// Port of <c>GetMaxStackSize</c>. Zero, a negative, and <c>int.MaxValue</c> all mean "no
    /// practical limit", and all three become <c>0x7FFFFFFE</c> — one below <c>int.MaxValue</c>,
    /// because the client's own arithmetic overflows at the boundary.
    /// </remarks>
    public uint MaxStackSize =>
        Stackable is int.MaxValue or <= 0 ? 0x7FFFFFFE : (uint)Stackable;

    /// <summary>Whether the item is a container, and so needs a bag's extra fields.</summary>
    public bool IsBag => Class == ItemClass.Container || InventoryType == Data.Db.InventoryType.Bag;

    /// <summary>
    /// Damage per second, as the tooltip prints it.
    /// </summary>
    /// <remarks>
    /// Port of <c>getDPS</c>. The <c>× 500</c> is <c>× 1000 ÷ 2</c> folded together: milliseconds to
    /// seconds, and the average of the range.
    /// </remarks>
    public float DamagePerSecond
    {
        get
        {
            if (Delay == 0)
            {
                return 0f;
            }

            float total = 0f;

            foreach (ItemDamage damage in Damage)
            {
                total += damage.Min + damage.Max;
            }

            return total * 500f / Delay;
        }
    }
}

/// <summary>
/// <c>item_template</c>, loaded once at startup.
/// </summary>
/// <remarks>
/// Whole-table, like the creature and gameobject templates, and for the same reason: an item can be
/// asked about at any moment by any session, and a per-query round trip would put the database on
/// the tick's critical path.
/// </remarks>
public sealed class ItemTemplateStore
{
    private readonly Dictionary<uint, ItemTemplate> _templates = [];

    public int Count => _templates.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _templates.Clear();

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = SelectStatement;

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ItemTemplate template = Read(reader);
            _templates[template.Entry] = template;
        }
    }

    public bool TryGet(uint entry, out ItemTemplate? template) => _templates.TryGetValue(entry, out template);

    /// <summary>Every template, for the tests and the startup report.</summary>
    public IEnumerable<ItemTemplate> All => _templates.Values;

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} item templates");

    /// <summary>
    /// The column list, in the order <see cref="Read"/> reads it.
    /// </summary>
    /// <remarks>
    /// Written out rather than <c>SELECT *</c> so the ordinals below are fixed by this file and not
    /// by whatever order the table happens to have. A <c>SELECT *</c> against a table upstream has
    /// since reordered would read every column into the wrong field and fail silently.
    /// </remarks>
    private const string SelectStatement = """
        SELECT entry, class, subclass, SoundOverrideSubclass, name, displayid, Quality, Flags,
               FlagsExtra, BuyCount, BuyPrice, SellPrice, InventoryType, AllowableClass,
               AllowableRace, ItemLevel, RequiredLevel, RequiredSkill, RequiredSkillRank,
               requiredspell, requiredhonorrank, RequiredCityRank, RequiredReputationFaction,
               RequiredReputationRank, maxcount, stackable, ContainerSlots, StatsCount,
               stat_type1, stat_value1, stat_type2, stat_value2, stat_type3, stat_value3,
               stat_type4, stat_value4, stat_type5, stat_value5, stat_type6, stat_value6,
               stat_type7, stat_value7, stat_type8, stat_value8, stat_type9, stat_value9,
               stat_type10, stat_value10,
               ScalingStatDistribution, ScalingStatValue,
               dmg_min1, dmg_max1, dmg_type1, dmg_min2, dmg_max2, dmg_type2,
               armor, holy_res, fire_res, nature_res, frost_res, shadow_res, arcane_res,
               delay, ammo_type, RangedModRange,
               spellid_1, spelltrigger_1, spellcharges_1, spellcooldown_1, spellcategory_1,
               spellcategorycooldown_1,
               spellid_2, spelltrigger_2, spellcharges_2, spellcooldown_2, spellcategory_2,
               spellcategorycooldown_2,
               spellid_3, spelltrigger_3, spellcharges_3, spellcooldown_3, spellcategory_3,
               spellcategorycooldown_3,
               spellid_4, spelltrigger_4, spellcharges_4, spellcooldown_4, spellcategory_4,
               spellcategorycooldown_4,
               spellid_5, spelltrigger_5, spellcharges_5, spellcooldown_5, spellcategory_5,
               spellcategorycooldown_5,
               bonding, description, PageText, LanguageID, PageMaterial, startquest, lockid,
               Material, sheath, RandomProperty, RandomSuffix, block, itemset, MaxDurability,
               area, Map, BagFamily, TotemCategory,
               socketColor_1, socketContent_1, socketColor_2, socketContent_2,
               socketColor_3, socketContent_3,
               socketBonus, GemProperties, RequiredDisenchantSkill, ArmorDamageModifier,
               duration, ItemLimitCategory, HolidayId
        FROM item_template
        """;

    private static ItemTemplate Read(MySqlDataReader reader)
    {
        ItemStat[] stats = new ItemStat[ItemConstants.MaxStats];

        for (int i = 0; i < stats.Length; i++)
        {
            stats[i] = new ItemStat(reader.GetByte(28 + (i * 2)), reader.GetInt16(29 + (i * 2)));
        }

        ItemDamage[] damage = new ItemDamage[ItemConstants.MaxDamages];

        for (int i = 0; i < damage.Length; i++)
        {
            damage[i] = new ItemDamage(
                reader.GetFloat(50 + (i * 3)), reader.GetFloat(51 + (i * 3)), reader.GetByte(52 + (i * 3)));
        }

        ItemSpell[] spells = new ItemSpell[ItemConstants.MaxSpells];

        for (int i = 0; i < spells.Length; i++)
        {
            spells[i] = new ItemSpell(
                SpellId: reader.GetInt32(66 + (i * 6)),
                Trigger: reader.GetByte(67 + (i * 6)),
                Charges: reader.GetInt16(68 + (i * 6)),
                CooldownMs: reader.GetInt32(69 + (i * 6)),
                Category: reader.GetUInt16(70 + (i * 6)),
                CategoryCooldownMs: reader.GetInt32(71 + (i * 6)));
        }

        ItemSocket[] sockets = new ItemSocket[ItemConstants.MaxSockets];

        for (int i = 0; i < sockets.Length; i++)
        {
            sockets[i] = new ItemSocket(reader.GetSByte(114 + (i * 2)), reader.GetInt32(115 + (i * 2)));
        }

        return new ItemTemplate(
            Entry: reader.GetUInt32(0),
            Class: reader.GetByte(1),
            SubClass: reader.GetByte(2),
            SoundOverrideSubclass: reader.GetSByte(3),
            Name: reader.GetString(4),
            DisplayId: reader.GetUInt32(5),
            Quality: reader.GetByte(6),
            Flags: reader.GetUInt32(7),
            FlagsExtra: reader.GetUInt32(8),
            BuyCount: reader.GetByte(9),
            BuyPrice: reader.GetInt64(10),
            SellPrice: reader.GetUInt32(11),
            InventoryType: reader.GetByte(12),
            AllowableClass: reader.GetInt32(13),
            AllowableRace: reader.GetInt32(14),
            ItemLevel: reader.GetUInt16(15),
            RequiredLevel: reader.GetByte(16),
            RequiredSkill: reader.GetUInt16(17),
            RequiredSkillRank: reader.GetUInt16(18),
            RequiredSpell: reader.GetUInt32(19),
            RequiredHonorRank: reader.GetUInt32(20),
            RequiredCityRank: reader.GetUInt32(21),
            RequiredReputationFaction: reader.GetUInt16(22),
            RequiredReputationRank: reader.GetUInt16(23),
            MaxCount: reader.GetInt32(24),

            // Nullable in the table and nowhere else. A null stack is one, not zero — zero would
            // make the item impossible to hold.
            Stackable: reader.IsDBNull(25) ? 1 : reader.GetInt32(25),
            ContainerSlots: reader.GetByte(26),
            StatsCount: reader.GetByte(27),
            Stats: stats,
            ScalingStatDistribution: reader.GetInt16(48),
            ScalingStatValue: reader.GetUInt32(49),
            Damage: damage,
            Armor: reader.GetUInt16(56),
            HolyResistance: reader.GetByte(57),
            FireResistance: reader.GetByte(58),
            NatureResistance: reader.GetByte(59),
            FrostResistance: reader.GetByte(60),
            ShadowResistance: reader.GetByte(61),
            ArcaneResistance: reader.GetByte(62),
            Delay: reader.GetUInt16(63),
            AmmoType: reader.GetByte(64),
            RangedModRange: reader.GetFloat(65),
            Spells: spells,
            Bonding: reader.GetByte(96),
            Description: reader.GetString(97),
            PageText: reader.GetUInt32(98),
            LanguageId: reader.GetByte(99),
            PageMaterial: reader.GetByte(100),
            StartQuest: reader.GetUInt32(101),
            LockId: reader.GetUInt32(102),
            Material: reader.GetSByte(103),
            Sheath: reader.GetByte(104),
            RandomProperty: reader.GetInt32(105),
            RandomSuffix: reader.GetUInt32(106),
            Block: reader.GetUInt32(107),
            ItemSet: reader.GetUInt32(108),
            MaxDurability: reader.GetUInt16(109),
            Area: reader.GetUInt32(110),
            Map: reader.GetInt16(111),
            BagFamily: reader.GetInt32(112),
            TotemCategory: reader.GetInt32(113),
            Sockets: sockets,
            SocketBonus: reader.GetInt32(120),
            GemProperties: reader.GetInt32(121),
            RequiredDisenchantSkill: reader.GetInt16(122),
            ArmorDamageModifier: reader.GetFloat(123),
            DurationSeconds: reader.GetUInt32(124),
            ItemLimitCategory: reader.GetInt16(125),
            HolidayId: reader.GetUInt32(126));
    }
}
