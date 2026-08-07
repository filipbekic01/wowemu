using WowEmu.Data.Client;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The four skill tables, read against a real client.
/// </summary>
/// <remarks>
/// None of them is useful alone, and the interesting rule — how a skill's bar is scaled — is not a
/// column in any of them. It is derived from the category and from whether the skill has a tier
/// row, in an order that is not the order the categories suggest.
/// </remarks>
public sealed class SkillLineTests
{
    [RequiresClientDataFact]
    public void TheTablesLoad()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        // 150 + 241 + 26 + 10219, which is what the four headers say.
        Assert.Equal(10636, stores.Skills.TotalRows);
    }

    /// <summary>Weapon skills climb with level; that is the common case.</summary>
    [RequiresClientDataFact]
    public void AWeaponSkill_ClimbsWithLevel() =>
        Assert.Equal(SkillRange.Level, RangeOf(SkillType.Swords, Human, Warrior));

    /// <summary>
    /// Armour proficiencies are a grey bar with nothing to fill.
    /// </summary>
    /// <remarks>
    /// You either can wear plate or cannot. Treating it as level-scaled would draw a progress bar on
    /// a skill that has no progress, which is what the category check is there to prevent.
    /// </remarks>
    [RequiresClientDataFact]
    public void AnArmourProficiency_IsAGreyBar() =>
        Assert.Equal(SkillRange.Mono, RangeOf(SkillType.PlateMail, Human, Warrior));

    /// <summary>Languages are learned whole, at a flat 300.</summary>
    [RequiresClientDataFact]
    public void ALanguage_IsFlatThreeHundred()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        SkillRaceClassInfoEntry info = Assert.IsType<SkillRaceClassInfoEntry>(
            stores.Skills.RaceClassInfo(Common, Human, Warrior));

        Assert.Equal(SkillRange.Language, stores.Skills.RangeOf(info));

        Player player = InventoryFixture.Player(level: 1, race: Human, characterClass: Warrior);

        Assert.True(SkillLearning.LearnDefault(player, stores.Skills, Common));
        Assert.Equal(SkillLines.LanguageValue, player.Skills.Value(Common));
        Assert.Equal(SkillLines.LanguageValue, player.Skills.MaxValue(Common));
    }

    /// <summary>
    /// A tier row wins over the category, which is what makes a profession a profession.
    /// </summary>
    /// <remarks>
    /// Mining is a profession by category too, so it does not prove the ordering on its own — the
    /// check that matters is that the tier lookup happens first, since a skill with a tier row is
    /// ranked whatever else it claims to be.
    /// </remarks>
    [RequiresClientDataFact]
    public void AProfession_IsRanked()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        SkillRaceClassInfoEntry info = Assert.IsType<SkillRaceClassInfoEntry>(
            stores.Skills.RaceClassInfo(Mining, Human, Warrior));

        Assert.NotEqual(0u, info.SkillTierId);
        Assert.NotNull(stores.Skills.Tier(info.SkillTierId));
        Assert.Equal(SkillRange.Rank, stores.Skills.RangeOf(info));
    }

    /// <summary>
    /// Runeforging is named outright, against what its own category says.
    /// </summary>
    /// <remarks>
    /// It is a class skill by category, which would make it a level bar climbing to five times a
    /// death knight's level. It is a proficiency you either have or do not, and upstream hard-codes
    /// the exception rather than correcting the table — worth pinning, because the next person to
    /// read <c>RangeOf</c> will wonder whether the special case is still needed.
    /// </remarks>
    [RequiresClientDataFact]
    public void Runeforging_IsAnExceptionToItsOwnCategory()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        SkillLineEntry line = Assert.IsType<SkillLineEntry>(stores.Skills.Line(SkillType.Runeforging));

        // The category alone would say Level; the named exception is what makes it Mono.
        Assert.Equal(SkillCategory.Class, line.CategoryId);
        Assert.Equal(SkillRange.Mono, RangeOf(SkillType.Runeforging, Human, DeathKnight));
    }

    /// <summary>
    /// A class that may not have a skill gets no row, and learning it does nothing.
    /// </summary>
    /// <remarks>
    /// Null from <c>RaceClassInfo</c> is a real answer, not missing data — it is how the tables say
    /// a warrior is not a death knight.
    /// </remarks>
    [RequiresClientDataFact]
    public void ASkillTheClassMayNotHave_IsRefused()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Null(stores.Skills.RaceClassInfo(SkillType.Runeforging, Human, Warrior));

        Player player = InventoryFixture.Player(level: 20, race: Human, characterClass: Warrior);

        Assert.False(SkillLearning.LearnDefault(player, stores.Skills, SkillType.Runeforging));
        Assert.False(player.Skills.Has(SkillType.Runeforging));
    }

    /// <summary>A level-scaled skill starts at 1 and is capped at five times the level.</summary>
    [RequiresClientDataFact]
    public void ALevelSkill_StartsAtOneAndCapsAtFiveTimesLevel()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Player player = InventoryFixture.Player(level: 10, race: Human, characterClass: Warrior);

        Assert.True(SkillLearning.LearnDefault(player, stores.Skills, SkillType.Swords));

        Assert.Equal(1, player.Skills.PureValue(SkillType.Swords));
        Assert.Equal(50, player.Skills.PureMaxValue(SkillType.Swords));
    }

    /// <summary>
    /// A death knight starts level-scaled skills near their own cap rather than at 1.
    /// </summary>
    /// <remarks>
    /// They begin at 55. A level-55 character with 1 weapon skill misses almost everything it swings
    /// at, which would make the starting zone unplayable — this is the one class-specific rule in
    /// the whole of <c>LearnDefaultSkill</c> and it is load-bearing.
    /// </remarks>
    [RequiresClientDataFact]
    public void ADeathKnight_StartsNearTheCap()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Player knight = InventoryFixture.Player(level: 55, race: Human, characterClass: DeathKnight);

        Assert.True(SkillLearning.LearnDefault(knight, stores.Skills, SkillType.Swords));

        // (55 - 1) * 5, against a cap of 275.
        Assert.Equal(270, knight.Skills.PureValue(SkillType.Swords));
        Assert.Equal(275, knight.Skills.PureMaxValue(SkillType.Swords));
    }

    /// <summary>Fist Weapons inherits Unarmed rather than starting fresh.</summary>
    /// <remarks>
    /// The same skill as far as the fantasy is concerned. Starting at 1 would undo every point a
    /// character had already put into Unarmed the moment they picked up a fist weapon.
    /// </remarks>
    [RequiresClientDataFact]
    public void FistWeapons_InheritUnarmed()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Player player = InventoryFixture.Player(level: 20, race: Human, characterClass: Warrior);

        player.Skills.Set(SkillType.Unarmed, 0, 73, 100);

        Assert.True(SkillLearning.LearnDefault(player, stores.Skills, SkillType.FistWeapons));
        Assert.Equal(73, player.Skills.PureValue(SkillType.FistWeapons));
    }

    /// <summary>
    /// Levelling up raises the ceiling and leaves the value where practice left it.
    /// </summary>
    /// <remarks>
    /// This is what makes weapon skill something you keep up rather than something you are given.
    /// Raising the value too would make every level-up a free maximum.
    /// </remarks>
    [RequiresClientDataFact]
    public void LevellingUp_RaisesTheCeilingOnly()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Player player = InventoryFixture.Player(level: 10, race: Human, characterClass: Warrior);

        SkillLearning.LearnDefault(player, stores.Skills, SkillType.Swords);
        player.Skills.Set(SkillType.Swords, 0, 40, 50);

        player.Level = 11;
        SkillLearning.UpdateForLevel(player, stores.Skills);

        Assert.Equal(40, player.Skills.PureValue(SkillType.Swords));
        Assert.Equal(55, player.Skills.PureMaxValue(SkillType.Swords));
    }

    /// <summary>
    /// Levelling leaves the grey bars alone.
    /// </summary>
    /// <remarks>
    /// A maximum of 1 is how upstream recognises them here without re-deriving the range. Raising it
    /// turns "you can wear plate" into a skill with 274 points to grind.
    /// </remarks>
    [RequiresClientDataFact]
    public void LevellingUp_LeavesGreyBarsAlone()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Player player = InventoryFixture.Player(level: 10, race: Human, characterClass: Warrior);

        SkillLearning.LearnDefault(player, stores.Skills, SkillType.PlateMail);

        Assert.Equal(1, player.Skills.PureMaxValue(SkillType.PlateMail));

        player.Level = 40;
        SkillLearning.UpdateForLevel(player, stores.Skills);

        Assert.Equal(1, player.Skills.PureMaxValue(SkillType.PlateMail));
    }

    /// <summary>
    /// Learning a spell brings its skill with it, which is how anyone gets a skill at all.
    /// </summary>
    /// <remarks>
    /// Nothing grants skills directly — no table lists a warrior's starting skills. They arrive
    /// through <c>SkillLineAbility</c> rows whose acquire method says the spell and the skill live
    /// and die together.
    /// </remarks>
    [RequiresClientDataFact]
    public void LearningASpell_BringsItsSkill()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Player player = InventoryFixture.Player(level: 10, race: Human, characterClass: Warrior);

        SkillLearning.LearnSkillsFromSpell(player, stores.Skills, PlateMailSpell);

        Assert.True(player.Skills.Has(SkillType.PlateMail));
    }

    /// <summary>
    /// Learning it again does not reset what practice has built up.
    /// </summary>
    /// <remarks>
    /// The grant is skipped for anything already known. It matters because the spellbook is walked
    /// on every login to repair characters made before skills existed — without the guard, that
    /// repair would put a 300 miner back to 1 every time they logged in.
    /// </remarks>
    [RequiresClientDataFact]
    public void LearningTheSameSpellTwice_DoesNotResetTheSkill()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Player player = InventoryFixture.Player(level: 20, race: Human, characterClass: Warrior);

        SkillLearning.LearnSkillsFromSpell(player, stores.Skills, SwordsSpell);

        // Without this the test passes whether or not the spell grants anything, since the value
        // below would simply survive untouched.
        Assert.True(player.Skills.Has(SkillType.Swords));

        player.Skills.Set(SkillType.Swords, 0, 88, 100);

        SkillLearning.LearnSkillsFromSpell(player, stores.Skills, SwordsSpell);

        Assert.Equal(88, player.Skills.PureValue(SkillType.Swords));
    }

    /// <summary>
    /// A skill flagged always-max is granted at its ceiling, not at 1, and tracks the level.
    /// </summary>
    /// <remarks>
    /// The class lines are the ones that carry the flag — Arms is 5/5 on a level-1 warrior. They are
    /// not skills you practise, and starting them at 1 would show a warrior an Arms bar with four
    /// points to grind that nothing in the game ever fills.
    /// </remarks>
    [RequiresClientDataFact]
    public void AnAlwaysMaxSkill_IsGrantedAtItsCeiling()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        SkillRaceClassInfoEntry info = Assert.IsType<SkillRaceClassInfoEntry>(
            stores.Skills.RaceClassInfo(Arms, Human, Warrior));

        Assert.NotEqual(0u, info.Flags & SkillRaceClassInfoEntry.AlwaysMaxValue);

        Player player = InventoryFixture.Player(level: 1, race: Human, characterClass: Warrior);

        Assert.True(SkillLearning.LearnDefault(player, stores.Skills, Arms));
        Assert.Equal(5, player.Skills.PureValue(Arms));
        Assert.Equal(5, player.Skills.PureMaxValue(Arms));

        // And it keeps up with the level rather than being left behind.
        player.Level = 10;
        SkillLearning.UpdateForLevel(player, stores.Skills);

        Assert.Equal(50, player.Skills.PureValue(Arms));
        Assert.Equal(50, player.Skills.PureMaxValue(Arms));
    }

    private static SkillRange RangeOf(uint skillId, byte race, byte characterClass)
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        SkillRaceClassInfoEntry info = Assert.IsType<SkillRaceClassInfoEntry>(
            stores.Skills.RaceClassInfo(skillId, race, characterClass));

        return stores.Skills.RangeOf(info);
    }

    private const byte Human = 1;
    private const byte Warrior = 1;
    private const byte DeathKnight = 6;

    /// <summary>Language: Common.</summary>
    private const uint Common = 98;

    private const uint Mining = 186;

    /// <summary>The warrior class line, which carries the always-max flag.</summary>
    private const uint Arms = 26;

    /// <summary>"Plate Mail" — the proficiency spell a warrior learns at 40.</summary>
    private const uint PlateMailSpell = 750;

    /// <summary>"Swords" — the proficiency spell.</summary>
    private const uint SwordsSpell = 201;
}
