using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Quest drops, which are shown only to people on the quest.
/// </summary>
/// <remarks>
/// The <c>NeedsQuest</c> flag was loaded from the loot template and never read, so a quest drop was
/// visible to everyone — which makes it worthless to the person who needs it, since anyone can take
/// it first.
/// </remarks>
public sealed class QuestLootTests
{
    /// <summary>Someone on the quest needs the item.</summary>
    [Fact]
    public void SomeoneOnTheQuest_NeedsIt()
    {
        (Player player, QuestStore quests) = OnTheQuest();

        Assert.True(player.Quests.NeedsItem(WolfPelt, quests));
    }

    /// <summary>
    /// Someone who has not taken the quest does not.
    /// </summary>
    /// <remarks>
    /// The whole point. A quest drop everyone can see is one the questing player rarely gets.
    /// </remarks>
    [Fact]
    public void SomeoneNotOnTheQuest_DoesNot()
    {
        Player player = InventoryFixture.Player();

        Assert.False(player.Quests.NeedsItem(WolfPelt, Quests()));
    }

    /// <summary>
    /// Someone who already has enough does not need another.
    /// </summary>
    /// <remarks>
    /// A player holding all eight has no claim on a ninth, and showing it to them is how a full
    /// player blocks somebody else from a drop.
    /// </remarks>
    [Fact]
    public void SomeoneWithEnough_DoesNotNeedAnother()
    {
        (Player player, QuestStore quests) = OnTheQuest();

        QuestProgress progress = Assert.IsType<QuestProgress>(player.Quests.Find(QuestId));
        progress.Collected[0] = Required;

        Assert.False(player.Quests.NeedsItem(WolfPelt, quests));
    }

    /// <summary>Part way through, they still need more.</summary>
    [Fact]
    public void PartWayThrough_TheyStillNeedMore()
    {
        (Player player, QuestStore quests) = OnTheQuest();

        QuestProgress progress = Assert.IsType<QuestProgress>(player.Quests.Find(QuestId));
        progress.Collected[0] = (ushort)(Required - 1);

        Assert.True(player.Quests.NeedsItem(WolfPelt, quests));
    }

    /// <summary>A different item on the same quest is not this one.</summary>
    [Fact]
    public void ADifferentItem_IsNotNeeded()
    {
        (Player player, QuestStore quests) = OnTheQuest();

        Assert.False(player.Quests.NeedsItem(SomethingElse, quests));
    }

    private const uint QuestId = 5000;
    private const uint WolfPelt = 4000;
    private const uint SomethingElse = 4001;
    private const ushort Required = 8;

    private static QuestStore Quests()
    {
        QuestItem[] items = new QuestItem[QuestConstants.MaxItemObjectives];
        items[0] = new QuestItem(WolfPelt, Required);

        return QuestFixture.Store(QuestFixture.Build(id: QuestId) with { RequiredItems = items });
    }

    private static (Player Player, QuestStore Quests) OnTheQuest()
    {
        QuestStore quests = Quests();
        Player player = InventoryFixture.Player();

        quests.TryGet(QuestId, out QuestTemplate? quest);
        player.Quests.Accept(quest!);

        return (player, quests);
    }
}
