using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The skill block: 127 slots of three words, and the packing that makes them readable.
/// </summary>
/// <remarks>
/// There is no skill packet. The client draws its skill window entirely from these fields, so
/// writing them correctly is the whole of making a skill appear — and a word packed the wrong way
/// round shows a plausible skill at a wrong value rather than nothing at all.
/// </remarks>
public sealed class PlayerSkillsTests
{
    /// <summary>The id and the step share the first word, id in the low half.</summary>
    [Fact]
    public void TheFirstWord_HoldsTheIdAndTheStep()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, step: 2, value: 30, maxValue: 50);

        uint packed = player.Fields.GetUInt32(UpdateFields.PLAYER_SKILL_INFO_1_1);

        Assert.Equal(SkillType.Swords, packed & 0xFFFF);
        Assert.Equal(2u, packed >> 16);
    }

    /// <summary>The value and its maximum share the second word, value in the low half.</summary>
    [Fact]
    public void TheSecondWord_HoldsTheValueAndTheMaximum()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, step: 0, value: 30, maxValue: 50);

        uint packed = player.Fields.GetUInt32(UpdateFields.PLAYER_SKILL_INFO_1_1 + 1);

        Assert.Equal(30u, packed & 0xFFFF);
        Assert.Equal(50u, packed >> 16);
    }

    /// <summary>
    /// The bonus word is the other way round — temporary low, permanent high.
    /// </summary>
    /// <remarks>
    /// <c>SKILL_TEMP_BONUS</c> is <c>PAIR32_LOPART</c> and <c>SKILL_PERM_BONUS</c> is the high half,
    /// which is the opposite order from the two words above it. Getting it backwards makes an
    /// enchantment survive a logout and a profession book vanish on one, and neither looks like a
    /// packing bug from the outside.
    /// </remarks>
    [Fact]
    public void TheBonusWord_PutsTheTemporaryOneFirst()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 30, 50);
        player.Skills.SetBonus(SkillType.Swords, temporary: 5, permanent: 9);

        uint packed = player.Fields.GetUInt32(UpdateFields.PLAYER_SKILL_INFO_1_1 + 2);

        Assert.Equal(5u, packed & 0xFFFF);
        Assert.Equal(9u, packed >> 16);

        Assert.Equal(5, player.Skills.TemporaryBonus(SkillType.Swords));
        Assert.Equal(9, player.Skills.PermanentBonus(SkillType.Swords));
    }

    /// <summary>Both bonuses count towards the value the client shows.</summary>
    [Fact]
    public void TheShownValue_CountsBothBonuses()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 30, 50);
        player.Skills.SetBonus(SkillType.Swords, temporary: 5, permanent: 9);

        Assert.Equal(44, player.Skills.Value(SkillType.Swords));
        Assert.Equal(39, player.Skills.BaseValue(SkillType.Swords));
        Assert.Equal(30, player.Skills.PureValue(SkillType.Swords));
        Assert.Equal(64, player.Skills.MaxValue(SkillType.Swords));
        Assert.Equal(50, player.Skills.PureMaxValue(SkillType.Swords));
    }

    /// <summary>
    /// A temporary bonus can be a penalty, and a penalty past zero reads as zero.
    /// </summary>
    /// <remarks>
    /// The bonus is signed but the field is not. Without the clamp a skill of 5 with a -10 debuff
    /// comes back as 65531, which is a character who suddenly cannot miss.
    /// </remarks>
    [Fact]
    public void APenaltyPastZero_ReadsAsZero()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 5, 50);
        player.Skills.SetBonus(SkillType.Swords, temporary: -10, permanent: 0);

        Assert.Equal(-10, player.Skills.TemporaryBonus(SkillType.Swords));
        Assert.Equal(0, player.Skills.Value(SkillType.Swords));
    }

    /// <summary>Skills take the first free slot, in the order they are learned.</summary>
    /// <remarks>
    /// Nothing sorts them, which is why the slot has to be remembered rather than computed from the
    /// id — and why the slot is not worth saving.
    /// </remarks>
    [Fact]
    public void Skills_TakeTheFirstFreeSlot()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 1, 5);
        player.Skills.Set(SkillType.Defense, 0, 1, 5);

        Assert.Equal(SkillType.Swords, player.Fields.GetUInt32(UpdateFields.PLAYER_SKILL_INFO_1_1) & 0xFFFF);
        Assert.Equal(
            SkillType.Defense,
            player.Fields.GetUInt32(UpdateFields.PLAYER_SKILL_INFO_1_1 + 3) & 0xFFFF);
    }

    /// <summary>
    /// Forgetting a skill clears its bonus word too.
    /// </summary>
    /// <remarks>
    /// Leaving it behind means the next skill to take the slot is born with somebody else's
    /// enchantment on it — which reads as a mysteriously good beginner rather than as a bug.
    /// </remarks>
    [Fact]
    public void ForgettingASkill_ClearsTheBonusToo()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 30, 50);
        player.Skills.SetBonus(SkillType.Swords, temporary: 5, permanent: 9);

        player.Skills.Set(SkillType.Swords, 0, value: 0, maxValue: 0);

        Assert.False(player.Skills.Has(SkillType.Swords));

        for (int word = 0; word < 3; word++)
        {
            Assert.Equal(0u, player.Fields.GetUInt32(UpdateFields.PLAYER_SKILL_INFO_1_1 + word));
        }

        // And the freed slot is clean for whoever takes it next.
        player.Skills.Set(SkillType.Defense, 0, 1, 5);

        Assert.Equal(0, player.Skills.PermanentBonus(SkillType.Defense));
    }

    /// <summary>Clearing a skill nobody has consumes nothing.</summary>
    /// <remarks>
    /// The alternative is a zeroed slot that <see cref="PlayerSkills.MaxSkills"/> still counts, so
    /// enough no-op removals would fill the block with nothing.
    /// </remarks>
    [Fact]
    public void ClearingAnUnknownSkill_ConsumesNoSlot()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        Assert.False(player.Skills.Set(SkillType.Swords, 0, value: 0, maxValue: 0));
        Assert.Equal(0, player.Skills.Count);
        Assert.Equal(0u, player.Fields.GetUInt32(UpdateFields.PLAYER_SKILL_INFO_1_1));
    }

    /// <summary>Setting a known skill again changes it in place rather than taking a second slot.</summary>
    [Fact]
    public void SettingAKnownSkill_StaysInItsSlot()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 30, 50);
        player.Skills.Set(SkillType.Swords, 0, 40, 60);

        Assert.Equal(1, player.Skills.Count);
        Assert.Equal(40, player.Skills.PureValue(SkillType.Swords));
        Assert.Equal(0u, player.Fields.GetUInt32(UpdateFields.PLAYER_SKILL_INFO_1_1 + 3));
    }

    /// <summary>The block holds 127 skills and refuses the 128th.</summary>
    /// <remarks>
    /// Refusing rather than overwriting: the field block has room for 128 triples and upstream uses
    /// 127, so the last one stays empty. Writing into it would be invisible until something else
    /// used that field.
    /// </remarks>
    [Fact]
    public void TheBlock_HoldsAHundredAndTwentySeven()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        for (uint skill = 1; skill <= PlayerSkills.MaxSkills; skill++)
        {
            Assert.True(player.Skills.Set(skill, 0, 1, 5), $"skill {skill} should fit");
        }

        Assert.Equal(PlayerSkills.MaxSkills, player.Skills.Count);
        Assert.False(player.Skills.Set(9999, 0, 1, 5));
    }

    /// <summary>Saving stores the value without its bonuses.</summary>
    /// <remarks>
    /// Saving the shown value would fold a temporary buff into the character permanently — a point
    /// per logout for as long as it was up.
    /// </remarks>
    [Fact]
    public void Saving_LeavesTheBonusesOut()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, step: 2, value: 30, maxValue: 50);
        player.Skills.SetBonus(SkillType.Swords, temporary: 5, permanent: 9);

        (ushort skill, ushort value, ushort max, ushort step) = Assert.Single(player.Skills.Snapshot());

        Assert.Equal(SkillType.Swords, skill);
        Assert.Equal(30, value);
        Assert.Equal(50, max);
        Assert.Equal(2, step);
    }

    /// <summary>Restoring replaces what is there rather than adding to it.</summary>
    [Fact]
    public void Restoring_ReplacesWhatIsThere()
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 30, 50);

        player.Skills.Restore([((ushort)SkillType.Defense, (ushort)12, (ushort)50, (ushort)0)]);

        Assert.False(player.Skills.Has(SkillType.Swords));
        Assert.Equal(12, player.Skills.PureValue(SkillType.Defense));
        Assert.Equal(1, player.Skills.Count);
    }
}

/// <summary>
/// What a player's weapon and defence skill actually read once they have skills.
/// </summary>
public sealed class PlayerSkillCombatTests
{
    /// <summary>
    /// A character with no skill granted falls back to the level cap, not to zero.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing half of the wiring today, because nothing has granted skills to
    /// characters made before the system existed. Zero here means missing almost every swing, which
    /// is a far worse wrong answer than being slightly too good.
    /// </remarks>
    [Fact]
    public void WithoutTheSkill_TheLevelCapIsUsed()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);

        Assert.Equal(100, player.WeaponSkillValue);
        Assert.Equal(100, player.DefenseSkillValue);
    }

    /// <summary>With the skill granted, the granted value is what counts.</summary>
    [Fact]
    public void WithTheSkill_TheSkillIsUsed()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);

        player.Skills.Set(SkillType.Unarmed, 0, 42, 100);
        player.Skills.Set(SkillType.Defense, 0, 37, 100);

        Assert.Equal(42, player.WeaponSkillValue);
        Assert.Equal(37, player.DefenseSkillValue);
    }

    /// <summary>
    /// The weapon in hand picks the skill, so two weapons read two different numbers.
    /// </summary>
    /// <remarks>
    /// The whole point of per-weapon skill. A character who has practised swords and never held an
    /// axe should swing the axe worse, and reading one skill for everything erases that.
    /// </remarks>
    [Fact]
    public void TheWeaponInHand_PicksTheSkill()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);

        player.Skills.Set(SkillType.Swords, 0, 100, 100);
        player.Skills.Set(SkillType.Axes, 0, 5, 100);

        InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 1, itemClass: ItemClass.Weapon, subClass: SwordSubClass),
            MainHand);

        Assert.Equal(100, player.WeaponSkillValue);

        player.Inventory.Restore(
            MainHand,
            Item.Create(
                InventoryFixture.NextGuid(),
                ItemFixture.Build(entry: 2, itemClass: ItemClass.Weapon, subClass: AxeSubClass),
                player.Guid));

        Assert.Equal(5, player.WeaponSkillValue);
    }

    /// <summary>An empty hand is Unarmed, which is a skill like any other.</summary>
    [Fact]
    public void AnEmptyHand_IsUnarmed()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);

        player.Skills.Set(SkillType.Unarmed, 0, 11, 100);

        Assert.Equal(11, player.WeaponSkillValue);
    }

    /// <summary>
    /// The weapon skill table is indexed by subclass, holes and all.
    /// </summary>
    /// <remarks>
    /// Four subclasses have no skill — the obsolete exotic pair, bear, cat and miscellaneous. They
    /// have to stay as zeroes in place: dropping them shifts every skill after them onto the wrong
    /// weapon, which is the kind of mistake that makes daggers use a wand's skill.
    /// </remarks>
    [Theory]
    [InlineData(0, SkillType.Axes)]
    [InlineData(1, SkillType.TwoHandedAxes)]
    [InlineData(7, SkillType.Swords)]
    [InlineData(9, 0u)]                            // the hole
    [InlineData(10, SkillType.Staves)]
    [InlineData(15, SkillType.Daggers)]
    [InlineData(19, SkillType.Wands)]
    [InlineData(20, SkillType.Fishing)]
    [InlineData(99, 0u)]                           // past the end
    public void WeaponSubclasses_MapToTheirSkill(byte subClass, uint expected) =>
        Assert.Equal(expected, SkillType.ForItem(ItemClass.Weapon, subClass));

    /// <summary>Armour maps to a proficiency, and its own table has holes too.</summary>
    [Theory]
    [InlineData(0, 0u)]                            // miscellaneous
    [InlineData(1, SkillType.Cloth)]
    [InlineData(4, SkillType.PlateMail)]
    [InlineData(6, SkillType.Shield)]
    [InlineData(7, 0u)]
    public void ArmourSubclasses_MapToTheirProficiency(byte subClass, uint expected) =>
        Assert.Equal(expected, SkillType.ForItem(ItemClass.Armor, subClass));

    /// <summary>Anything that is neither has no skill at all.</summary>
    [Fact]
    public void SomethingThatIsNeither_HasNoSkill() =>
        Assert.Equal(0u, SkillType.ForItem(ItemClass.Misc, 1));

    private const byte SwordSubClass = 7;
    private const byte AxeSubClass = 0;

    private static ItemPosition MainHand => new(InventorySlots.Backpack, InventorySlots.MainHand);
}
