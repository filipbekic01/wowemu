using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.WorldServer;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Whether a trainer will teach a line, now that skills exist to check against.
/// </summary>
/// <remarks>
/// One rule used twice — the colour the client draws and the check that refuses a purchase are the
/// same question. Answering it in two places is how a trainer ends up showing something in green
/// and then declining to sell it.
/// </remarks>
public sealed class TrainerRulesTests
{
    /// <summary>Something already known is red, whatever else is true of it.</summary>
    [Fact]
    public void AKnownSpell_IsRed()
    {
        Player player = InventoryFixture.Player(level: 60, proficiencies: false);
        player.Spells.Learn(Taught);

        Assert.Equal(TrainerSpellState.Red, TrainerRules.StateOf(player, Line()));
    }

    /// <summary>Too low a level is grey.</summary>
    [Fact]
    public void TooLowALevel_IsGrey()
    {
        Player player = InventoryFixture.Player(level: 5, proficiencies: false);

        Assert.Equal(TrainerSpellState.Grey, TrainerRules.StateOf(player, Line(requiredLevel: 20)));
    }

    /// <summary>A line with no requirements at all is teachable.</summary>
    [Fact]
    public void ALineWithNoRequirements_IsGreen()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);

        Assert.Equal(TrainerSpellState.Green, TrainerRules.StateOf(player, Line()));
    }

    /// <summary>
    /// A skill the character does not have refuses the line.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the change. Before skills existed every one of these passed, so a
    /// profession trainer offered its entire list in green to anyone who could afford it.
    /// </remarks>
    [Fact]
    public void ASkillTheyDoNotHave_IsGrey()
    {
        Player player = InventoryFixture.Player(level: 60, proficiencies: false);

        Assert.Equal(
            TrainerSpellState.Grey,
            TrainerRules.StateOf(player, Line(requiredSkill: (ushort)SkillType.Swords, requiredRank: 100)));
    }

    /// <summary>Having the skill but not the rank still refuses.</summary>
    [Fact]
    public void HavingTheSkillButNotTheRank_IsGrey()
    {
        Player player = InventoryFixture.Player(level: 60, proficiencies: false);
        player.Skills.Set(SkillType.Swords, 0, 99, 300);

        Assert.Equal(
            TrainerSpellState.Grey,
            TrainerRules.StateOf(player, Line(requiredSkill: (ushort)SkillType.Swords, requiredRank: 100)));
    }

    /// <summary>Meeting the rank exactly is enough.</summary>
    [Fact]
    public void MeetingTheRankExactly_IsGreen()
    {
        Player player = InventoryFixture.Player(level: 60, proficiencies: false);
        player.Skills.Set(SkillType.Swords, 0, 100, 300);

        Assert.Equal(
            TrainerSpellState.Green,
            TrainerRules.StateOf(player, Line(requiredSkill: (ushort)SkillType.Swords, requiredRank: 100)));
    }

    /// <summary>
    /// A required skill with a rank of zero still means the skill must be present.
    /// </summary>
    /// <remarks>
    /// Two conditions, not one. A rank check alone passes trivially — a missing skill reads as zero,
    /// and zero is not less than zero — so every such line would be offered to a character who has
    /// never touched the profession. Most rows are exactly this shape, so getting it wrong would
    /// leave the feature barely working at all.
    /// </remarks>
    [Fact]
    public void ARankOfZero_StillRequiresTheSkill()
    {
        Player without = InventoryFixture.Player(level: 60, proficiencies: false);

        Assert.Equal(
            TrainerSpellState.Grey,
            TrainerRules.StateOf(without, Line(requiredSkill: (ushort)SkillType.Swords, requiredRank: 0)));

        Player with = InventoryFixture.Player(level: 60, proficiencies: false);
        with.Skills.Set(SkillType.Swords, 0, 1, 300);

        Assert.Equal(
            TrainerSpellState.Green,
            TrainerRules.StateOf(with, Line(requiredSkill: (ushort)SkillType.Swords, requiredRank: 0)));
    }

    /// <summary>The bonused value counts, since that is what the client shows.</summary>
    /// <remarks>
    /// A profession book or an enchanted glove is meant to open recipes, which is the only reason to
    /// wear one — reading the stored value instead would make those bonuses purely cosmetic.
    /// </remarks>
    [Fact]
    public void ABonus_CountsTowardsTheRequirement()
    {
        Player player = InventoryFixture.Player(level: 60, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 95, 300);
        player.Skills.SetBonus(SkillType.Swords, temporary: 0, permanent: 10);

        Assert.Equal(
            TrainerSpellState.Green,
            TrainerRules.StateOf(player, Line(requiredSkill: (ushort)SkillType.Swords, requiredRank: 100)));
    }

    private const uint Taught = 674;

    private static TrainerSpell Line(
        ushort requiredSkill = 0, ushort requiredRank = 0, byte requiredLevel = 0) =>
        new(
            TrainerId: 1,
            SpellId: Taught,
            MoneyCost: 100,
            RequiredSkill: requiredSkill,
            RequiredSkillRank: requiredRank,
            RequiredLevel: requiredLevel,
            RequiredSpellId: 0);
}
