using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.WorldServer;

namespace WowEmu.Tests.Unit;

/// <summary>
/// What an account is allowed to make: expansion gating, the death knight unlock, and factions.
/// </summary>
/// <remarks>
/// The client greys out every one of these, which is exactly why they have to be checked here — a
/// client that has been told to ignore its own UI sends whatever it likes.
/// </remarks>
public sealed class CharacterCreationRuleTests
{
    private const byte Human = 1;
    private const byte Orc = 2;
    private const byte Dwarf = 3;
    private const byte BloodElf = 10;
    private const byte Draenei = 11;

    private const byte Warrior = 1;
    private const byte DeathKnight = 6;

    // ------------------------------------------------------------------ expansion

    /// <summary>
    /// A TBC race is refused to a vanilla account.
    /// </summary>
    /// <remarks>
    /// Blood elves and draenei carry expansion 1 in <c>ChrRaces.dbc</c>; everything older carries 0.
    /// </remarks>
    [RequiresClientDataFact]
    public void ATbcRace_NeedsTbc()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.Expansion,
            CharacterCreationRules.CheckExpansion(
                BloodElf, Warrior, allowedExpansion: 0, stores.Races, stores.Classes));

        Assert.Equal(
            CharCreateResult.Ok,
            CharacterCreationRules.CheckExpansion(
                BloodElf, Warrior, allowedExpansion: 1, stores.Races, stores.Classes));
    }

    /// <summary>
    /// A death knight is refused to a TBC account, on class grounds rather than race.
    /// </summary>
    /// <remarks>
    /// The two refusals are different sentences in the client. Sending the race one for a class
    /// problem tells a human warrior's owner to buy Wrath for being human.
    /// </remarks>
    [RequiresClientDataFact]
    public void ADeathKnight_IsRefusedOnClassGrounds()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.ExpansionClass,
            CharacterCreationRules.CheckExpansion(
                Human, DeathKnight, allowedExpansion: 1, stores.Races, stores.Classes));

        Assert.Equal(
            CharCreateResult.Ok,
            CharacterCreationRules.CheckExpansion(
                Human, DeathKnight, allowedExpansion: 2, stores.Races, stores.Classes));
    }

    /// <summary>A vanilla race and class pass at every expansion.</summary>
    [RequiresClientDataFact]
    public void AVanillaPair_AlwaysPasses()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.Ok,
            CharacterCreationRules.CheckExpansion(
                Human, Warrior, allowedExpansion: 0, stores.Races, stores.Classes));
    }

    // ------------------------------------------------------------------ death knights

    /// <summary>
    /// A death knight needs an existing character at the level, not a new one.
    /// </summary>
    /// <remarks>
    /// The character being made has no level yet. Reading its level instead of the roster's refuses
    /// every death knight on the server, forever.
    /// </remarks>
    [RequiresClientDataFact]
    public void ADeathKnight_NeedsAnExistingFiftyFive()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.LevelRequirement,
            Roster([(Human, Warrior, 54)], Human, DeathKnight, stores));

        Assert.Equal(
            CharCreateResult.Ok,
            Roster([(Human, Warrior, 55)], Human, DeathKnight, stores));
    }

    /// <summary>An empty account cannot start with a death knight.</summary>
    [RequiresClientDataFact]
    public void AnEmptyAccount_CannotStartWithADeathKnight()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.LevelRequirement, Roster([], Human, DeathKnight, stores));
    }

    /// <summary>
    /// A second death knight is refused even with the level met.
    /// </summary>
    /// <remarks>
    /// A different refusal from the level one: the player has met the requirement and is out of
    /// slots, which is not the same problem and not the same fix.
    /// </remarks>
    [RequiresClientDataFact]
    public void ASecondDeathKnight_IsRefusedForTheSlot()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.UniqueClassLimit,
            Roster([(Human, Warrior, 80), (Human, DeathKnight, 60)], Human, DeathKnight, stores));
    }

    /// <summary>An ordinary class is unaffected by the level rule.</summary>
    /// <remarks>
    /// Easy to get wrong by hoisting the check: a level gate applied to every class would leave a
    /// brand-new account unable to make its first character.
    /// </remarks>
    [RequiresClientDataFact]
    public void AnOrdinaryClass_IgnoresTheLevelRule()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(CharCreateResult.Ok, Roster([], Human, Warrior, stores));
    }

    // ------------------------------------------------------------------ factions

    /// <summary>
    /// With two-sided accounts off, the other faction is refused.
    /// </summary>
    /// <remarks>
    /// This is the whole of what players mean by a PvP realm's account rule.
    /// </remarks>
    [RequiresClientDataFact]
    public void WithTwoSidedOff_TheOtherFaction_IsRefused()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.PvpTeamsViolation,
            Roster([(Human, Warrior, 10)], Orc, Warrior, stores, twoSided: false));
    }

    /// <summary>
    /// A different race on the same side is fine.
    /// </summary>
    /// <remarks>
    /// The rule is about teams, not races. Comparing races refuses a human account a dwarf, which
    /// is the obvious way to write this and completely wrong.
    /// </remarks>
    [RequiresClientDataFact]
    public void WithTwoSidedOff_TheSameSide_IsFine()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.Ok,
            Roster([(Human, Warrior, 10)], Dwarf, Warrior, stores, twoSided: false));
    }

    /// <summary>A TBC race joins the side its file puts it on.</summary>
    /// <remarks>
    /// Draenei are Alliance and blood elves Horde, and neither is adjacent to the older races in
    /// any ordering — a hard-coded race list gets both backwards.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheTbcRaces_JoinTheRightSides()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharacterCreationRules.TeamOf(Human, stores.Races),
            CharacterCreationRules.TeamOf(Draenei, stores.Races));

        Assert.Equal(
            CharacterCreationRules.TeamOf(Orc, stores.Races),
            CharacterCreationRules.TeamOf(BloodElf, stores.Races));

        Assert.NotEqual(
            CharacterCreationRules.TeamOf(Human, stores.Races),
            CharacterCreationRules.TeamOf(Orc, stores.Races));
    }

    /// <summary>With two-sided accounts on, the other faction is allowed.</summary>
    [RequiresClientDataFact]
    public void WithTwoSidedOn_TheOtherFaction_IsAllowed()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            CharCreateResult.Ok,
            Roster([(Human, Warrior, 10)], Orc, Warrior, stores, twoSided: true));
    }

    // ------------------------------------------------------------------ helpers

    private static byte Roster(
        (byte Race, byte Class, byte Level)[] existing,
        byte race,
        byte characterClass,
        DbcStores stores,
        bool twoSided = true) =>
        CharacterCreationRules.CheckRoster(
            [.. existing.Select((row, index) => Summary((uint)index + 1, row.Race, row.Class, row.Level))],
            race,
            characterClass,
            allowTwoSideAccounts: twoSided,
            minLevelForHeroic: 55,
            heroicPerRealm: 1,
            stores.Races);

    private static CharacterSummary Summary(uint id, byte race, byte characterClass, byte level) =>
        new(id, $"C{id}", race, characterClass, 0, 0, 0, 0, 0, 0, level, 0, 0, 0, 0, 0, 0, 0, 0);
}
