using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Vendors that can run out, and refill.
/// </summary>
/// <remarks>
/// Every vendor had infinite supply. The data has said otherwise all along — <c>maxcount</c> and
/// <c>incrtime</c> were being loaded and ignored — so this is the runtime half rather than new data.
/// <para>
/// The clock is passed in throughout, so a restock interval can be crossed without a test waiting
/// for it.
/// </para>
/// </remarks>
public sealed class VendorStockTests
{
    private const uint ItemId = 1234;

    /// <summary>
    /// A maxcount of zero is unlimited, and never enters the ledger.
    /// </summary>
    /// <remarks>
    /// Nearly every row in the table. Reading it as a real count sells out every vendor in the game
    /// before the first purchase, and tracking it would put an entry in the ledger for each.
    /// </remarks>
    [Fact]
    public void UnlimitedStock_IsNeverTracked()
    {
        VendorStock stock = new();
        VendorItem unlimited = Item(maxCount: 0);

        Assert.Equal(0u, stock.Available(unlimited, buyCount: 1, now: 0));

        stock.Take(unlimited, buyCount: 1, used: 5, now: 0);

        Assert.Equal(0, stock.TrackedCount);
    }

    /// <summary>Buying takes stock off the shelf.</summary>
    [Fact]
    public void Buying_ReducesWhatIsLeft()
    {
        VendorStock stock = new();
        VendorItem limited = Item(maxCount: 5);

        Assert.Equal(5u, stock.Available(limited, buyCount: 1, now: 0));

        Assert.Equal(3u, stock.Take(limited, buyCount: 1, used: 2, now: 0));
        Assert.Equal(3u, stock.Available(limited, buyCount: 1, now: 0));
    }

    /// <summary>Buying more than is there empties the shelf rather than going negative.</summary>
    [Fact]
    public void BuyingMoreThanIsThere_StopsAtZero()
    {
        VendorStock stock = new();
        VendorItem limited = Item(maxCount: 3);

        Assert.Equal(0u, stock.Take(limited, buyCount: 1, used: 10, now: 0));
    }

    /// <summary>
    /// Stock comes back after the restock interval, and only on whole intervals.
    /// </summary>
    /// <remarks>
    /// Partial credit would let a vendor drip stock back continuously, which is not what the column
    /// means — it is a period, not a rate.
    /// </remarks>
    [Fact]
    public void Stock_ComesBackOnWholeIntervalsOnly()
    {
        VendorStock stock = new();
        VendorItem limited = Item(maxCount: 5, restockSeconds: 100);

        stock.Take(limited, buyCount: 1, used: 4, now: 0);
        Assert.Equal(1u, stock.Available(limited, buyCount: 1, now: 0));

        // Most of an interval is not an interval.
        Assert.Equal(1u, stock.Available(limited, buyCount: 1, now: 99));

        // Two whole intervals bring back two.
        Assert.Equal(3u, stock.Available(limited, buyCount: 1, now: 200));
    }

    /// <summary>Restocking refills by a purchase's worth per interval, not by one item.</summary>
    /// <remarks>
    /// A vendor selling arrows in stacks of twenty restocks twenty at a time. Refilling one at a
    /// time would take twenty times as long as the data asks for.
    /// </remarks>
    [Fact]
    public void Restocking_AddsAPurchasesWorthPerInterval()
    {
        VendorStock stock = new();
        VendorItem limited = Item(maxCount: 100, restockSeconds: 100);

        // Five stacks of twenty, in items — maxcount counts arrows, not purchases.
        stock.Take(limited, buyCount: 20, used: 100, now: 0);
        Assert.Equal(0u, stock.Available(limited, buyCount: 20, now: 0));

        // One interval brings back one stack's worth, not one arrow.
        Assert.Equal(20u, stock.Available(limited, buyCount: 20, now: 100));
    }

    /// <summary>
    /// A fully restocked item is forgotten rather than kept at its maximum.
    /// </summary>
    /// <remarks>
    /// The ledger is sparse in time as well as in rows: a vendor left alone long enough holds no
    /// state at all, which is the same thing as being untouched.
    /// </remarks>
    [Fact]
    public void OnceFull_TheItemIsForgotten()
    {
        VendorStock stock = new();
        VendorItem limited = Item(maxCount: 5, restockSeconds: 10);

        stock.Take(limited, buyCount: 1, used: 3, now: 0);
        Assert.Equal(1, stock.TrackedCount);

        Assert.Equal(5u, stock.Available(limited, buyCount: 1, now: 1000));
        Assert.Equal(0, stock.TrackedCount);
    }

    /// <summary>Restocking never overshoots the maximum.</summary>
    [Fact]
    public void Restocking_NeverExceedsTheMaximum()
    {
        VendorStock stock = new();
        VendorItem limited = Item(maxCount: 5, restockSeconds: 1);

        stock.Take(limited, buyCount: 1, used: 1, now: 0);

        Assert.Equal(5u, stock.Available(limited, buyCount: 1, now: 100_000));
    }

    /// <summary>
    /// A restock interval of zero means it never comes back.
    /// </summary>
    /// <remarks>
    /// The column carries zeros, and dividing by one would throw. Treating it as "instant" instead
    /// would make a limited item effectively unlimited, which is the opposite of what the row says.
    /// </remarks>
    [Fact]
    public void ARestockIntervalOfZero_NeverRefills()
    {
        VendorStock stock = new();
        VendorItem limited = Item(maxCount: 5, restockSeconds: 0);

        stock.Take(limited, buyCount: 1, used: 5, now: 0);

        Assert.Equal(0u, stock.Available(limited, buyCount: 1, now: long.MaxValue / 2));
    }

    /// <summary>
    /// A purchase after a long absence is charged against the refilled count.
    /// </summary>
    /// <remarks>
    /// Taking restocks first is upstream's order and is what stops a vendor being permanently empty
    /// because nobody looked at it between purchases.
    /// </remarks>
    [Fact]
    public void TakingAfterALongAbsence_RestocksFirst()
    {
        VendorStock stock = new();
        VendorItem limited = Item(maxCount: 5, restockSeconds: 100);

        stock.Take(limited, buyCount: 1, used: 5, now: 0);
        Assert.Equal(0u, stock.Available(limited, buyCount: 1, now: 0));

        // Three intervals later there are three, and buying one leaves two.
        Assert.Equal(2u, stock.Take(limited, buyCount: 1, used: 1, now: 300));
    }

    /// <summary>Two vendors of the same template keep their own shelves.</summary>
    /// <remarks>
    /// The reason the ledger hangs off the creature rather than the entry. Sharing it would have
    /// buying the last of something from one innkeeper empty every other innkeeper in the world.
    /// </remarks>
    [Fact]
    public void TwoVendors_DoNotShareStock()
    {
        Creature first = CreatureFixture.Build();
        Creature second = CreatureFixture.Build();

        VendorItem limited = Item(maxCount: 2);

        first.Stock.Take(limited, buyCount: 1, used: 2, now: 0);

        Assert.Equal(0u, first.Stock.Available(limited, buyCount: 1, now: 0));
        Assert.Equal(2u, second.Stock.Available(limited, buyCount: 1, now: 0));
    }

    /// <summary>A vendor that dies and respawns has restocked.</summary>
    [Fact]
    public void ARespawningVendor_ComesBackStocked()
    {
        Creature vendor = CreatureFixture.Build();
        VendorItem limited = Item(maxCount: 4);

        vendor.Stock.Take(limited, buyCount: 1, used: 4, now: 0);
        Assert.Equal(0u, vendor.Stock.Available(limited, buyCount: 1, now: 0));

        vendor.Respawn();

        Assert.Equal(4u, vendor.Stock.Available(limited, buyCount: 1, now: 0));
    }

    private static VendorItem Item(byte maxCount, uint restockSeconds = 0) =>
        new(Entry: 1, Slot: 0, ItemId: ItemId, MaxCount: maxCount, RestockSeconds: restockSeconds,
            ExtendedCost: 0);
}
