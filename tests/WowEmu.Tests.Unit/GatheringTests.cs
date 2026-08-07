using WowEmu.Data.Client;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The gathering loot sources: skinning, pickpocketing, fishing and disenchanting.
/// </summary>
public sealed class GatheringTests
{
    // ------------------------------------------------------------------ skinning

    /// <summary>
    /// Which skill skins a creature depends on its type flags, not on the word "skinning".
    /// </summary>
    /// <remarks>
    /// Three flags redirect it to herbalism, mining or engineering. That is how a herb-covered
    /// elemental is looted, and assuming skinning refuses a herbalist the plant they can see.
    /// </remarks>
    [Fact]
    public void TheSkinningSkill_ComesFromTheTypeFlags()
    {
        Assert.Equal(SkillType.Skinning, Skinning.SkillFor(0));
        Assert.Equal(SkillType.Herbalism, Skinning.SkillFor(CreatureTypeFlags.SkinWithHerbalism));
        Assert.Equal(SkillType.Mining, Skinning.SkillFor(CreatureTypeFlags.SkinWithMining));
        Assert.Equal(SkillType.Engineering, Skinning.SkillFor(CreatureTypeFlags.SkinWithEngineering));
    }

    /// <summary>
    /// Crossing 100 skill changes which formula applies, and the requirement drops.
    /// </summary>
    /// <remarks>
    /// <b>The two formulas cross over.</b> For a level 40 corpse the low-skill rule asks 300 and the
    /// high-skill rule asks 200 — so a skinner who reaches 100 can suddenly touch corpses that were
    /// refused a moment earlier. Collapsing them into one formula is the obvious tidy-up and it
    /// changes what a mid-skill skinner may do.
    /// </remarks>
    [Fact]
    public void CrossingAHundred_LowersTheRequirement()
    {
        Assert.Equal(300, Skinning.RequiredSkill(targetLevel: 40, skillValue: 99));
        Assert.Equal(200, Skinning.RequiredSkill(targetLevel: 40, skillValue: 100));
    }

    /// <summary>
    /// The skill-up requirement floors at zero; the cast requirement does not.
    /// </summary>
    /// <remarks>
    /// A third formula, and genuinely different. Below level 10 the gain requirement is 0 while the
    /// cast requirement goes negative — sharing one of them either makes every low corpse a
    /// guaranteed skill-up or makes none of them skinnable.
    /// </remarks>
    [Fact]
    public void TheGainRequirement_FloorsAtZero()
    {
        Assert.Equal(0, Skinning.GainRequirement(5));
        Assert.True(Skinning.RequiredSkill(targetLevel: 5, skillValue: 1) < 0);
    }

    /// <summary>
    /// A skinner below the requirement cannot skin.
    /// </summary>
    /// <remarks>
    /// Both cases stay under 100 skill on purpose, so they exercise the low-skill formula rather
    /// than silently crossing into the other one.
    /// </remarks>
    [Theory]
    [InlineData(15, 49, false)]
    [InlineData(15, 50, true)]
    public void SkinningNeedsTheSkill(int level, int skill, bool expected) =>
        Assert.Equal(expected, Skinning.CanSkin(level, skill));

    // ------------------------------------------------------------------ pickpocketing

    /// <summary>
    /// A picked pocket rolls twice, once on each level.
    /// </summary>
    /// <remarks>
    /// <b>Two rolls, not one on the sum.</b> The same range comes out either way, so a single roll
    /// looks correct — but it pays the maximum as often as anything else, where the real thing is a
    /// triangular curve that rarely does.
    /// </remarks>
    [Fact]
    public void APickedPocket_RollsTwice()
    {
        List<int> bounds = [];

        Pickpocketing.Money(targetLevel: 40, pickerLevel: 30, bound =>
        {
            bounds.Add(bound);
            return bound - 1;
        });

        // urand(0, level / 2), so the exclusive bound is level / 2 + 1 — one per level.
        Assert.Equal([21, 16], bounds);
    }

    /// <summary>And multiplies the pair by ten.</summary>
    /// <remarks>
    /// Both rolls at zero pays nothing at all, which is a real outcome rather than a bug — a pocket
    /// can genuinely be empty of coin.
    /// </remarks>
    [Fact]
    public void APickedPocket_PaysTenTimesTheRolls()
    {
        Assert.Equal(0u, Pickpocketing.Money(targetLevel: 10, pickerLevel: 10, _ => 0));
        Assert.Equal(60u, Pickpocketing.Money(targetLevel: 10, pickerLevel: 10, _ => 3));
    }

    /// <summary>
    /// The pocket stays empty longer than a minute.
    /// </summary>
    /// <remarks>
    /// A minute <i>plus</i> the corpse and respawn delays, so the pocket cannot refill before the
    /// creature could plausibly have died and come back. A flat minute lets a rogue farm one guard.
    /// </remarks>
    [Fact]
    public void ThePickpocketCooldown_OutlastsTheRespawn() =>
        Assert.Equal(60 + 30 + 300, Pickpocketing.CooldownSeconds(corpseDelaySeconds: 30, respawnSeconds: 300));

    // ------------------------------------------------------------------ fishing

    /// <summary>
    /// The fishing chance is squared, not linear.
    /// </summary>
    /// <remarks>
    /// Half the required skill gives 25%, not 50%. A linear reading makes early fishing dramatically
    /// easier than it should be, and looks perfectly reasonable while doing it.
    /// </remarks>
    [Fact]
    public void TheFishingChance_IsSquared()
    {
        // zoneSkill 5 → noMiss 100, so a skill of 50 is exactly half.
        Assert.Equal(25, Fishing.SuccessChance(skill: 50, zoneSkill: 5));
        Assert.NotEqual(50, Fishing.SuccessChance(skill: 50, zoneSkill: 5));
    }

    /// <summary>Ninety-five above the zone's skill is a guaranteed catch.</summary>
    [Fact]
    public void NinetyFiveAbove_NeverMisses()
    {
        Assert.Equal(100, Fishing.SuccessChance(skill: 195, zoneSkill: 100));
        Assert.True(Fishing.SuccessChance(skill: 194, zoneSkill: 100) < 100);
    }

    /// <summary>
    /// Hopeless water still gives the occasional catch.
    /// </summary>
    /// <remarks>
    /// Floored at 1. A zero would make a zone unfishable forever rather than merely painful, and
    /// the squaring reaches zero long before the skill does.
    /// </remarks>
    [Fact]
    public void HopelessWater_StillFloorsAtOne() =>
        Assert.Equal(1, Fishing.SuccessChance(skill: 1, zoneSkill: 500));

    // ------------------------------------------------------------------ disenchanting

    /// <summary>
    /// An item with no disenchant id can never be disenchanted, at any skill.
    /// </summary>
    /// <remarks>
    /// Most items are like this, and it is not a skill problem — reporting it as one sends a maxed
    /// enchanter off to level a skill that is already maxed.
    /// </remarks>
    [Fact]
    public void AnItemWithNoLootId_IsNeverDisenchantable() =>
        Assert.False(Disenchant.CanDisenchant(disenchantId: 0, requiredSkill: 0, skillValue: 450));

    /// <summary>And one that has an id still needs the skill.</summary>
    [Fact]
    public void ADisenchantableItem_StillNeedsTheSkill()
    {
        Assert.False(Disenchant.CanDisenchant(disenchantId: 12, requiredSkill: 225, skillValue: 224));
        Assert.True(Disenchant.CanDisenchant(disenchantId: 12, requiredSkill: 225, skillValue: 225));
    }

    // ------------------------------------------------------------------ loot kinds

    /// <summary>
    /// The two fishing kinds the client cannot read go out as plain fishing.
    /// </summary>
    /// <remarks>
    /// 20 and 22 exist to pick a table server-side. Putting either on the wire gets a window the
    /// client has no layout for.
    /// </remarks>
    [Fact]
    public void TheUnsendableFishingKinds_GoOutAsFishing()
    {
        Assert.Equal(LootKind.Fishing, LootKind.OnWire(LootKind.FishingHole));
        Assert.Equal(LootKind.Fishing, LootKind.OnWire(LootKind.FishingJunk));
        Assert.Equal(LootKind.Skinning, LootKind.OnWire(LootKind.Skinning));
    }
}
