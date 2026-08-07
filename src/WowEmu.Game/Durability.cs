using WowEmu.Data.Client;

namespace WowEmu.Game;

/// <summary>
/// Wearing gear out, and paying to have it mended.
/// </summary>
/// <remarks>
/// Port of <c>Player::DurabilityLoss</c>, <c>DurabilityLossAll</c> and <c>DurabilityRepair</c>.
/// <para>
/// Nothing wore anything out before this, so the durability the items already carried never moved
/// and repair had nothing to repair. The pair belongs together: one creates the need and the other
/// answers it, and either alone is a system that does nothing.
/// </para>
/// </remarks>
public static class Durability
{
    /// <summary>
    /// What dying costs, as a fraction of each item's maximum. <c>RATE_DURABILITY_LOSS_ON_DEATH</c>.
    /// </summary>
    /// <remarks>
    /// Ten percent, and <b>equipment only</b> — upstream passes <c>inventory: false</c> on death, so
    /// what is worn wears out and what is carried does not. Releasing to a spirit healer costs
    /// another twenty-five percent of everything including the bags, which is a separate call and
    /// not made here.
    /// </remarks>
    public const double DeathLoss = 0.10;

    /// <summary>
    /// Wears one item by a fraction of its maximum.
    /// </summary>
    /// <remarks>
    /// <b>Always at least one point.</b> Ten percent of a nine-durability item rounds to zero, and
    /// without the floor those items would never wear out at all — which is most of what a level-one
    /// character is wearing.
    /// </remarks>
    public static void Lose(Item item, double fraction)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (fraction <= 0 || item.MaxDurability == 0)
        {
            return;
        }

        uint points = Math.Max((uint)(item.MaxDurability * fraction), 1u);

        item.Durability = item.Durability > points ? item.Durability - points : 0;
    }

    /// <summary>
    /// Wears everything a player has equipped.
    /// </summary>
    /// <param name="inventory">
    /// Whether to wear carried items too. False on death, true when a spirit healer resurrects you.
    /// </param>
    public static void LoseAll(Player player, double fraction, bool inventory = false)
    {
        ArgumentNullException.ThrowIfNull(player);

        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.Equipped(slot) is { } worn)
            {
                Lose(worn, fraction);
            }
        }

        if (!inventory)
        {
            return;
        }

        foreach (Item carried in player.Inventory.Carried)
        {
            // Bags and keys have no durability of their own, so they fall out of this by having a
            // maximum of zero rather than by being filtered for. Carried rather than All, because
            // All includes what is worn — and the loop above has already worn that.
            Lose(carried, fraction);
        }
    }

    /// <summary>
    /// What mending one item costs, in copper.
    /// </summary>
    /// <param name="discount">
    /// The reputation discount, 1.0 for none. Passed in rather than looked up because reputation
    /// does not exist yet and this should not pretend otherwise.
    /// </param>
    /// <remarks>
    /// Port of the cost half of <c>Player::DurabilityRepair</c>. Three lookups multiply together:
    /// the points lost, a multiplier chosen by the item's <i>level</i> and kind, and a modifier
    /// chosen by its quality.
    /// <para>
    /// <b>The quality row is not the quality.</b> Upstream reads <c>(quality + 1) * 2</c>, so a
    /// common item takes row 4. Indexing by the quality itself finds a row that exists and is wrong.
    /// </para>
    /// <para>
    /// A cost that works out to zero is charged as one copper — upstream's own note is that this is
    /// a fix for artifact-quality items, whose modifier is zero and which would otherwise be free.
    /// </para>
    /// </remarks>
    public static uint RepairCost(
        Item item,
        DbcStore<DurabilityCostsEntry> costs,
        DbcStore<DurabilityQualityEntry> quality,
        float discount = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(quality);

        if (item.MaxDurability == 0 || item.Durability >= item.MaxDurability)
        {
            return 0;
        }

        uint lost = item.MaxDurability - item.Durability;

        if (!costs.TryGet(item.Template.ItemLevel, out DurabilityCostsEntry? cost) || cost is null)
        {
            // An item level the table does not describe. Upstream logs it and charges nothing rather
            // than guessing, which leaves the item repairable for free — the safe direction, since
            // the alternative is an unbounded bill.
            return 0;
        }

        if (!quality.TryGet(((uint)item.Template.Quality + 1) * 2, out DurabilityQualityEntry? modifier)
            || modifier is null)
        {
            return 0;
        }

        uint multiplier = cost.For(item.Template.Class, item.Template.SubClass);
        uint total = (uint)(lost * multiplier * (double)modifier.Modifier);

        total = (uint)(total * discount);

        return Math.Max(total, 1u);
    }

    /// <summary>
    /// Mends one item, if the player can pay for it.
    /// </summary>
    /// <returns>What it cost, or null when the player could not afford it.</returns>
    /// <remarks>
    /// The money comes off and the item is mended together, or neither happens. A repair that
    /// deducted first and then failed would be a charge for nothing.
    /// </remarks>
    public static uint? Repair(
        Player player,
        Item item,
        DbcStore<DurabilityCostsEntry> costs,
        DbcStore<DurabilityQualityEntry> quality,
        float discount = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);

        uint cost = RepairCost(item, costs, quality, discount);

        if (cost > player.Money)
        {
            return null;
        }

        player.Money -= cost;
        item.Durability = item.MaxDurability;

        return cost;
    }

    /// <summary>Mends everything a player is wearing and carrying, as far as the money goes.</summary>
    /// <remarks>
    /// Item by item rather than as one bill: upstream repairs what it can afford and stops, so a
    /// player short of the full amount still gets their weapon back rather than nothing.
    /// </remarks>
    public static uint RepairAll(
        Player player,
        DbcStore<DurabilityCostsEntry> costs,
        DbcStore<DurabilityQualityEntry> quality,
        float discount = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(player);

        uint spent = 0;

        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.Equipped(slot) is { } worn)
            {
                spent += Repair(player, worn, costs, quality, discount) ?? 0;
            }
        }

        foreach (Item carried in player.Inventory.Carried)
        {
            spent += Repair(player, carried, costs, quality, discount) ?? 0;
        }

        return spent;
    }
}
