using WowEmu.Data.Client;
using WowEmu.Data.Db;

namespace WowEmu.Game;

/// <summary>
/// Where an enchantment sits on an item. <c>EnchantmentSlot</c>.
/// </summary>
/// <remarks>
/// <b>Twelve slots, and only the first seven are ever shown to another player.</b> The last five
/// hold the random suffix's enchantments, which the client derives from the random-properties id
/// rather than reading — writing a permanent enchant into one of those is invisible and permanent.
/// </remarks>
public static class EnchantmentSlot
{
    /// <summary>A real enchant, the kind an enchanter applies.</summary>
    public const int Permanent = 0;

    /// <summary>An oil or a sharpening stone.</summary>
    public const int Temporary = 1;

    public const int Socket1 = 2;
    public const int Socket2 = 3;
    public const int Socket3 = 4;
    public const int Bonus = 5;
    public const int Prismatic = 6;

    /// <summary>How many slots another player's inspect can see.</summary>
    public const int Inspected = 7;

    /// <summary>The first of the five the random suffix owns.</summary>
    public const int Property0 = 7;

    /// <summary>How many there are in total.</summary>
    public const int Count = 12;

    /// <summary>How many the random suffix owns.</summary>
    public const int PropertyCount = Count - Property0;
}

/// <summary>
/// Random item properties: the "of the Bear" suffixes and the enchantments behind them.
/// </summary>
/// <remarks>
/// Port of <c>Item::GenerateItemRandomPropertyId</c>, <c>Item::SetItemRandomProperties</c> and
/// <c>GenerateEnchSuffixFactor</c>.
/// <para>
/// <b>The sign of the random-properties id picks the table.</b> Positive means
/// <c>ItemRandomProperties.dbc</c> and fixed amounts; negative means <c>ItemRandomSuffix.dbc</c>,
/// looked up by the absolute value, with amounts scaled by the item's suffix factor. Both tables
/// have rows at low ids, so reading the sign wrong finds a real row and applies the wrong suffix.
/// </para>
/// </remarks>
public static class ItemRandomProperties
{
    /// <summary>
    /// Rolls an item's random-properties id, signed.
    /// </summary>
    /// <returns>Zero for an item that has no random properties, which is most of them.</returns>
    /// <remarks>
    /// <b>An item may carry one of the two columns, never both.</b> Upstream logs and refuses a row
    /// that sets both rather than picking one, because there is no way to know which was meant.
    /// </remarks>
    public static int Generate(
        ItemTemplate template,
        ItemEnchantmentStore rolls,
        DbcStore<ItemRandomPropertiesEntry> properties,
        DbcStore<ItemRandomSuffixEntry> suffixes,
        Func<float> rollPercent)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rolls);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(suffixes);

        bool hasProperty = template.RandomProperty != 0;
        bool hasSuffix = template.RandomSuffix != 0;

        if (hasProperty == hasSuffix)
        {
            // Neither, or — malformed data — both.
            return 0;
        }

        if (hasProperty)
        {
            uint rolled = rolls.Roll((uint)template.RandomProperty, rollPercent);

            return properties.TryGet(rolled, out ItemRandomPropertiesEntry? entry) && entry is not null
                ? (int)entry.Id
                : 0;
        }

        uint rolledSuffix = rolls.Roll(template.RandomSuffix, rollPercent);

        return suffixes.TryGet(rolledSuffix, out ItemRandomSuffixEntry? suffix) && suffix is not null
            ? -(int)suffix.Id
            : 0;
    }

    /// <summary>
    /// The five enchantments a random-properties id puts on an item.
    /// </summary>
    /// <remarks>
    /// Written into <see cref="EnchantmentSlot.Property0"/> onwards, which is why an enchanter's
    /// work in slot 0 survives a suffix and vice versa.
    /// </remarks>
    public static uint[] EnchantmentsFor(
        int randomPropertyId,
        DbcStore<ItemRandomPropertiesEntry> properties,
        DbcStore<ItemRandomSuffixEntry> suffixes)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(suffixes);

        if (randomPropertyId > 0)
        {
            return properties.TryGet((uint)randomPropertyId, out ItemRandomPropertiesEntry? entry)
                && entry is not null
                    ? entry.Enchantments
                    : [];
        }

        if (randomPropertyId < 0
            && suffixes.TryGet((uint)(-randomPropertyId), out ItemRandomSuffixEntry? suffix)
            && suffix is not null)
        {
            return suffix.Enchantments;
        }

        return [];
    }

    /// <summary>
    /// The suffix factor, which is what scales a suffix's amounts to the item.
    /// </summary>
    /// <remarks>
    /// Port of <c>GenerateEnchSuffixFactor</c>. <b>Only for the suffix table</b> — an item with a
    /// positive random-properties id has fixed amounts and no factor at all.
    /// <para>
    /// The inventory type is collapsed into one of five coefficients rather than used directly: a
    /// chest and a two-hander share one, a ring and a cloak share another. Several types have no
    /// points at all and return zero.
    /// </para>
    /// </remarks>
    public static uint SuffixFactor(
        ItemTemplate template, DbcStore<RandomPropertyPointsEntry> points)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(points);

        if (template.RandomSuffix == 0)
        {
            return 0;
        }

        int coefficient = CoefficientFor(template.InventoryType);

        if (coefficient < 0
            || !points.TryGet(template.ItemLevel, out RandomPropertyPointsEntry? row)
            || row is null)
        {
            return 0;
        }

        return row.For(template.Quality, coefficient);
    }

    /// <summary>
    /// Which of the five point columns an inventory type reads.
    /// </summary>
    /// <returns>-1 for a type that has no random properties.</returns>
    public static int CoefficientFor(byte inventoryType) => inventoryType switch
    {
        // Head, body, chest, legs, two-hander, robe.
        1 or 4 or 5 or 7 or 17 or 20 => 0,

        // Shoulders, waist, feet, hands, trinket.
        3 or 6 or 8 or 10 or 12 => 1,

        // Neck, wrists, finger, shield, cloak, held-in-off-hand.
        2 or 9 or 11 or 14 or 16 or 23 => 2,

        // One-handers, main hand, off hand.
        13 or 21 or 22 => 3,

        // Ranged, thrown, ranged-right.
        15 or 25 or 26 => 4,

        // Non-equip, bag, tabard, ammo, quiver, relic — and anything unrecognised.
        _ => -1,
    };
}

/// <summary>
/// Rolling a random suffix onto a freshly made item, and writing it into the item's fields.
/// </summary>
/// <remarks>
/// Port of <c>Item::SetItemRandomProperties</c> together with the roll that precedes it. Kept apart
/// from <see cref="ItemRandomProperties"/> so the arithmetic stays testable without an item.
/// </remarks>
public static class RandomSuffixes
{
    /// <summary>
    /// Rolls a suffix onto a new item, if its template allows one.
    /// </summary>
    /// <returns>The signed id written, or zero when nothing was rolled.</returns>
    /// <remarks>
    /// <b>Rolled once, at creation.</b> Rolling on every look would give the same sword a different
    /// suffix each time it was inspected; rolling at load would give it a new one every login.
    /// </remarks>
    public static int Apply(
        Item item,
        ItemEnchantmentStore rolls,
        DbcStore<ItemRandomPropertiesEntry> properties,
        DbcStore<ItemRandomSuffixEntry> suffixes,
        DbcStore<RandomPropertyPointsEntry> points,
        Func<float> rollPercent)
    {
        ArgumentNullException.ThrowIfNull(item);

        int id = ItemRandomProperties.Generate(
            item.Template, rolls, properties, suffixes, rollPercent);

        if (id == 0)
        {
            return 0;
        }

        Restore(item, id, properties, suffixes, points);

        return id;
    }

    /// <summary>
    /// Puts a saved suffix back, without re-rolling.
    /// </summary>
    /// <remarks>
    /// The loading counterpart, and the reason the roll is separate: what an item rolled is part of
    /// the item, and re-rolling on load would change a player's gear under them every session.
    /// </remarks>
    public static void Restore(
        Item item,
        int randomPropertyId,
        DbcStore<ItemRandomPropertiesEntry> properties,
        DbcStore<ItemRandomSuffixEntry> suffixes,
        DbcStore<RandomPropertyPointsEntry> points)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(points);

        if (randomPropertyId == 0)
        {
            return;
        }

        item.RandomPropertiesId = randomPropertyId;

        // Only the scaled table has a factor. A positive id carries fixed amounts and setting one
        // would have the client scale amounts that are already final.
        if (randomPropertyId < 0)
        {
            item.SuffixFactor = ItemRandomProperties.SuffixFactor(item.Template, points);
        }

        uint[] enchantments =
            ItemRandomProperties.EnchantmentsFor(randomPropertyId, properties, suffixes);

        for (int i = 0; i < EnchantmentSlot.PropertyCount; i++)
        {
            item.SetEnchantment(
                EnchantmentSlot.Property0 + i, i < enchantments.Length ? enchantments[i] : 0);
        }
    }
}

/// <summary>
/// What an enchantment actually gives.
/// </summary>
/// <remarks>
/// Port of the stat half of <c>Player::ApplyEnchantment</c>. Only the effects that make sense
/// without a live spell system are resolved here; the spell-shaped ones are reported so the caller
/// can decide.
/// </remarks>
public static class Enchantments
{
    /// <summary>
    /// The amount one effect of an enchantment gives on a particular item.
    /// </summary>
    /// <param name="suffixFactor">
    /// The item's, from <see cref="ItemRandomProperties.SuffixFactor"/>. Zero for an enchantment
    /// that is not part of a scaled suffix, which is the ordinary case.
    /// </param>
    /// <remarks>
    /// <b>The allocation is in ten-thousandths.</b> A suffix row holding 3,200 means 32% of the
    /// item's points, so using it raw gives ten thousand times too much of every stat — an amount
    /// so large it reads as a different bug entirely.
    /// </remarks>
    public static int AmountFor(
        SpellItemEnchantmentEntry enchantment,
        int effect,
        uint suffixFactor,
        IReadOnlyList<uint>? allocations = null)
    {
        ArgumentNullException.ThrowIfNull(enchantment);

        if (effect < 0 || effect >= SpellItemEnchantmentEntry.Effects)
        {
            return 0;
        }

        if (allocations is null || effect >= allocations.Count || suffixFactor == 0)
        {
            return enchantment.Amounts[effect];
        }

        return (int)(allocations[effect] * suffixFactor / 10_000);
    }

    /// <summary>
    /// Whether an effect is one this server can resolve without casting anything.
    /// </summary>
    /// <remarks>
    /// Stats, resistances and flat weapon damage are arithmetic. The spell-shaped ones — procs,
    /// equip auras, use effects — need a live spell system behind them, and reporting them as
    /// applied when nothing casts them makes an enchanted weapon look correct and do nothing.
    /// </remarks>
    public static bool IsResolvable(uint effectType) =>
        effectType is EnchantmentEffect.Damage
            or EnchantmentEffect.Resistance
            or EnchantmentEffect.Stat;
}
