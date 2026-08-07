using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Items that run out of time.
/// </summary>
/// <remarks>
/// Durations were loaded, shown to the client and never decremented — so a mage could conjure a bag
/// full of bread and keep it indefinitely, with the client politely counting down a number the
/// server was ignoring.
/// </remarks>
public sealed class ItemDurationTests
{
    /// <summary>A timed item loses the time that passed.</summary>
    [Fact]
    public void ATimedItem_LosesTheTimeThatPassed()
    {
        Player player = InventoryFixture.Player();
        Item bread = Timed(player, seconds: 900);

        Assert.Empty(ItemDuration.Tick(player, 60));

        Assert.Equal(840u, bread.DurationSeconds);
    }

    /// <summary>
    /// An item with no duration is not a timed item.
    /// </summary>
    /// <remarks>
    /// Zero means permanent, not "expires immediately" — and almost everything in the game is zero.
    /// Reading it as a countdown destroys a player's entire inventory on the first tick.
    /// </remarks>
    [Fact]
    public void AnItemWithNoDuration_IsUntouched()
    {
        Player player = InventoryFixture.Player();

        Item permanent = InventoryFixture.Place(
            player, ItemFixture.Build(entry: 1), InventoryFixture.Backpack());

        Assert.Empty(ItemDuration.Tick(player, 10_000));

        Assert.NotNull(player.Inventory.PositionOf(permanent));
        Assert.Equal(0u, permanent.DurationSeconds);
    }

    /// <summary>Running out destroys the item and reports it.</summary>
    [Fact]
    public void RunningOut_DestroysTheItem()
    {
        Player player = InventoryFixture.Player();
        Item bread = Timed(player, seconds: 30);

        Item expired = Assert.Single(ItemDuration.Tick(player, 30));

        Assert.Same(bread, expired);
        Assert.Null(player.Inventory.PositionOf(bread));
    }

    /// <summary>Overshooting the remaining time destroys it rather than wrapping.</summary>
    /// <remarks>
    /// The subtraction is unsigned. Taking 60 from 30 without the check leaves roughly four billion
    /// seconds, so an item one tick from expiry becomes permanent.
    /// </remarks>
    [Fact]
    public void Overshooting_DestroysRatherThanWraps()
    {
        Player player = InventoryFixture.Player();
        Item bread = Timed(player, seconds: 30);

        Assert.Single(ItemDuration.Tick(player, 60));
        Assert.Null(player.Inventory.PositionOf(bread));
    }

    /// <summary>
    /// A timed item in the bank counts down too.
    /// </summary>
    /// <remarks>
    /// The point of a duration is that it runs out whatever you do with the thing — putting it away
    /// is not a way to stop the clock.
    /// </remarks>
    [Fact]
    public void ABankedItem_CountsDownToo()
    {
        Player player = InventoryFixture.Player();

        Item stored = InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 2, durationSeconds: 100),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.BankItemStart));

        ItemDuration.Tick(player, 40);

        Assert.Equal(60u, stored.DurationSeconds);
    }

    /// <summary>Several items tick together, and only the expired ones go.</summary>
    [Fact]
    public void SeveralItems_TickTogether()
    {
        Player player = InventoryFixture.Player();

        Item shortLived = InventoryFixture.Place(
            player, ItemFixture.Build(entry: 3, durationSeconds: 10), InventoryFixture.Backpack(0));

        Item longLived = InventoryFixture.Place(
            player, ItemFixture.Build(entry: 4, durationSeconds: 500), InventoryFixture.Backpack(1));

        Item expired = Assert.Single(ItemDuration.Tick(player, 20));

        Assert.Same(shortLived, expired);
        Assert.Equal(480u, longLived.DurationSeconds);
        Assert.NotNull(player.Inventory.PositionOf(longLived));
    }

    /// <summary>No time passing changes nothing.</summary>
    [Fact]
    public void NoTimePassing_ChangesNothing()
    {
        Player player = InventoryFixture.Player();
        Item bread = Timed(player, seconds: 900);

        Assert.Empty(ItemDuration.Tick(player, 0));

        Assert.Equal(900u, bread.DurationSeconds);
    }

    /// <summary>
    /// The map tick carries the sub-second remainder rather than discarding it.
    /// </summary>
    /// <remarks>
    /// The duration field is in seconds and the tick is in milliseconds. Dividing each tick and
    /// throwing away the remainder means a 100 ms tick contributes nothing at all — durations never
    /// move, and the bug looks like the feature simply not being wired up.
    /// </remarks>
    [Fact]
    public void TheMapTick_CarriesTheSubSecondRemainder()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        Item bread = InventoryFixture.Place(
            player, ItemFixture.Build(entry: 200, durationSeconds: 900), InventoryFixture.Backpack());

        // Ten ticks of a hundred milliseconds, none of which is a whole second on its own.
        for (int i = 0; i < 10; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Equal(899u, bread.DurationSeconds);
    }

    /// <summary>And a long tick spends every whole second in it.</summary>
    [Fact]
    public void ALongTick_SpendsEveryWholeSecond()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        Item bread = InventoryFixture.Place(
            player, ItemFixture.Build(entry: 201, durationSeconds: 900), InventoryFixture.Backpack());

        map.Update(gameplayDiff: 5_500, sessionDiff: 5_500);

        Assert.Equal(895u, bread.DurationSeconds);
    }

    private static Item Timed(Player player, uint seconds) =>
        InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 100, durationSeconds: seconds),
            InventoryFixture.Backpack());
}
