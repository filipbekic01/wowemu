namespace WowEmu.Game;

/// <summary>
/// Items that expire, and the ticking down of them.
/// </summary>
/// <remarks>
/// Port of <c>Item::UpdateDuration</c> and <c>Player::UpdateItemDuration</c>. Conjured food and
/// water, quest items with a time limit and a good deal of consumable content carry a duration that
/// was being loaded, shown to the client, and never decremented — so a mage could conjure a bag full
/// of bread and keep it indefinitely.
/// <para>
/// <b>Seconds, not milliseconds.</b> The field the client counts down is in seconds, and feeding it
/// a millisecond diff expires everything a thousand times too fast — which looks like items
/// vanishing on pickup rather than like a unit mistake.
/// </para>
/// </remarks>
public static class ItemDuration
{
    /// <summary>
    /// Advances every timed item a player holds, destroying whatever ran out.
    /// </summary>
    /// <param name="seconds">Whole seconds elapsed. Fractions are the caller's to accumulate.</param>
    /// <returns>The items that expired, so the caller can tell the client about each.</returns>
    /// <remarks>
    /// Everything held, worn or banked. A timed item does not stop counting because it was put away
    /// — the whole point of a duration is that it runs out whatever you do with the thing.
    /// </remarks>
    public static IReadOnlyList<Item> Tick(Player player, uint seconds)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (seconds == 0)
        {
            return [];
        }

        List<Item>? expired = null;

        // Materialised: destroying an item mutates the slot array this is walking.
        foreach ((ItemPosition position, Item item) in player.Inventory.AllWithPositions.ToArray())
        {
            if (item.DurationSeconds == 0)
            {
                continue;
            }

            if (item.DurationSeconds > seconds)
            {
                item.DurationSeconds -= seconds;
                continue;
            }

            // Ran out. Destroyed rather than left at zero, because zero is also what "no duration"
            // means — an item stranded there would read as permanent and never be looked at again.
            player.Inventory.Destroy(position, count: 0, out Item? removed);

            if (removed is not null)
            {
                (expired ??= []).Add(removed);
            }
        }

        return expired ?? (IReadOnlyList<Item>)[];
    }
}
