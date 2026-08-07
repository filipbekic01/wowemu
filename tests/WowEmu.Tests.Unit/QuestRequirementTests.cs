using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Quest requirements that need systems which did not exist until now.
/// </summary>
/// <remarks>
/// Skill and reputation gates were read from the table and ignored, so a profession quest was
/// offered to anyone and a faction's later work was offered before it trusted you.
/// </remarks>
public sealed class QuestRequirementTests
{
    /// <summary>A quest wanting a profession is refused without it.</summary>
    [Fact]
    public void AProfessionQuest_NeedsTheProfession()
    {
        Player player = Character();

        QuestTemplate quest = Quest() with
        {
            RequiredSkillId = (ushort)SkillType.Fishing,
            RequiredSkillPoints = 100,
        };

        Assert.Equal(QuestTakeResult.NotEnoughSkill, player.Quests.CanTake(quest));

        player.Skills.Set(SkillType.Fishing, 0, 100, 300);

        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(quest));
    }

    /// <summary>Having the skill but not the points is still a refusal.</summary>
    [Fact]
    public void NotEnoughPoints_IsStillARefusal()
    {
        Player player = Character();
        player.Skills.Set(SkillType.Fishing, 0, 99, 300);

        QuestTemplate quest = Quest() with
        {
            RequiredSkillId = (ushort)SkillType.Fishing,
            RequiredSkillPoints = 100,
        };

        Assert.Equal(QuestTakeResult.NotEnoughSkill, player.Quests.CanTake(quest));
    }

    /// <summary>A faction's later work waits until it trusts you.</summary>
    [Fact]
    public void AFactionsLaterWork_WaitsForTrust()
    {
        Player player = Character();

        QuestTemplate quest = Quest() with
        {
            RequiredMinRepFaction = Stormwind,
            RequiredMinRepValue = 9000,
        };

        Assert.Equal(QuestTakeResult.NotEnoughReputation, player.Quests.CanTake(quest));

        player.Reputation.Set(Stormwind, 9000);

        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(quest));
    }

    /// <summary>
    /// And its introductory work stops once it trusts you enough.
    /// </summary>
    /// <remarks>
    /// The half that is easy to miss. Checking only the minimum — the obvious one — leaves the
    /// starter quests on offer to an Exalted character forever.
    /// </remarks>
    [Fact]
    public void IntroductoryWork_StopsOnceTrusted()
    {
        Player player = Character();

        QuestTemplate quest = Quest() with
        {
            RequiredMaxRepFaction = Stormwind,
            RequiredMaxRepValue = 9000,
        };

        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(quest));

        player.Reputation.Set(Stormwind, 9000);

        Assert.Equal(QuestTakeResult.TooMuchReputation, player.Quests.CanTake(quest));
    }

    /// <summary>
    /// A requirement of zero is no requirement.
    /// </summary>
    /// <remarks>
    /// Almost every quest in the game leaves all four columns at zero, so reading them as real
    /// values makes the whole quest log unavailable.
    /// </remarks>
    [Fact]
    public void ZeroColumns_AreNoRequirement() =>
        Assert.Equal(QuestTakeResult.Ok, Character().Quests.CanTake(Quest()));

    private const ushort Stormwind = 72;

    private static Player Character() => InventoryFixture.Player(level: 20, proficiencies: false);

    private static QuestTemplate Quest() => QuestFixture.Build(id: 1, minLevel: 1);
}
