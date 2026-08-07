using System.Globalization;
using WowEmu.Data.Db;

namespace WowEmu.Game;

/// <summary>
/// What one vendor has left of its limited stock.
/// </summary>
/// <remarks>
/// Port of <c>Creature::GetVendorItemCurrentCount</c> and <c>UpdateVendorItemCurrentCount</c>.
/// <para>
/// <b>Per vendor, not per entry.</b> Two innkeepers of the same template each have their own
/// shelves — buying the last of something from one must not empty the other's, which is what a
/// store keyed on the creature entry would do.
/// </para>
/// <para>
/// <b>Only limited stock is tracked.</b> A <c>maxcount</c> of zero means unlimited and is nearly
/// every row in the table; those never enter the ledger at all, so the common case costs nothing.
/// </para>
/// <para>
/// The ledger is also <i>sparse in time</i>: an item is only recorded once something has been bought,
/// and it is dropped again the moment it has restocked to full. A vendor nobody has visited holds no
/// state, and one left alone long enough returns to holding none.
/// </para>
/// </remarks>
public sealed class VendorStock
{
    private readonly Dictionary<uint, Entry> _counts = [];

    /// <summary>How many items this vendor is currently tracking.</summary>
    public int TrackedCount => _counts.Count;

    /// <summary>
    /// How many of an item are on the shelf right now.
    /// </summary>
    /// <param name="item">The vendor row, which carries the maximum and the restock interval.</param>
    /// <param name="buyCount">
    /// How many the item's template says one purchase yields — restocking adds that many per
    /// interval rather than one, so a stack of five arrives five at a time.
    /// </param>
    /// <param name="now">Seconds since the epoch, passed in so a test need not wait.</param>
    /// <remarks>
    /// Restocking happens here rather than on a timer: nothing needs to know a vendor has refilled
    /// until somebody looks, and a timer over every vendor in the world would be work to arrive at
    /// the same answer.
    /// </remarks>
    public uint Available(VendorItem item, uint buyCount, long now)
    {
        if (item.MaxCount == 0)
        {
            // Unlimited. Upstream returns the maxcount itself here — which is zero — and every
            // caller reads zero as "no limit" rather than as "sold out".
            return 0;
        }

        if (!_counts.TryGetValue(item.ItemId, out Entry entry))
        {
            return item.MaxCount;
        }

        if (!TryRestock(item, buyCount, now, ref entry, out uint refilled))
        {
            return entry.Count;
        }

        if (refilled >= item.MaxCount)
        {
            // Back to full, so it stops being worth remembering.
            _counts.Remove(item.ItemId);
            return item.MaxCount;
        }

        _counts[item.ItemId] = new Entry(refilled, now);
        return refilled;
    }

    /// <summary>
    /// Takes some off the shelf, and says what is left.
    /// </summary>
    /// <remarks>
    /// Restocks first, exactly as upstream does, so a purchase after a long absence is charged
    /// against the refilled count rather than against what was left when the vendor was last seen.
    /// </remarks>
    public uint Take(VendorItem item, uint buyCount, uint used, long now)
    {
        if (item.MaxCount == 0)
        {
            return 0;
        }

        if (!_counts.TryGetValue(item.ItemId, out Entry entry))
        {
            uint remaining = item.MaxCount > used ? item.MaxCount - used : 0;

            _counts[item.ItemId] = new Entry(remaining, now);
            return remaining;
        }

        uint count = TryRestock(item, buyCount, now, ref entry, out uint refilled)
            ? Math.Min(refilled, item.MaxCount)
            : entry.Count;

        count = count > used ? count - used : 0;

        _counts[item.ItemId] = new Entry(count, now);
        return count;
    }

    /// <summary>Forgets every count. A respawning vendor comes back fully stocked.</summary>
    public void Clear() => _counts.Clear();

    /// <summary>
    /// Works out what an interval-based refill would bring the count to.
    /// </summary>
    /// <remarks>
    /// <b>False when no whole interval has passed</b>, which is the ordinary answer — the count is
    /// then left exactly as it was rather than being partially credited. A restock interval of zero
    /// would divide by it, and the table does carry those, so it is treated as "never refills".
    /// </remarks>
    private static bool TryRestock(
        VendorItem item, uint buyCount, long now, ref Entry entry, out uint refilled)
    {
        refilled = entry.Count;

        if (item.RestockSeconds == 0 || now < entry.LastRestocked + item.RestockSeconds)
        {
            return false;
        }

        long intervals = (now - entry.LastRestocked) / item.RestockSeconds;

        // One purchase's worth per interval, not one item — a vendor selling a stack of five
        // restocks five at a time.
        refilled = entry.Count + (uint)Math.Min(intervals * Math.Max(buyCount, 1u), item.MaxCount);

        return true;
    }

    private readonly record struct Entry(uint Count, long LastRestocked);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{TrackedCount} limited items tracked");
}
