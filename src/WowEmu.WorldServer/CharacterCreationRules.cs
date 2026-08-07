using WowEmu.Data.Client;
using WowEmu.Data.Db;

namespace WowEmu.WorldServer;

/// <summary>Why a character could not be made. The byte the client is sent.</summary>
/// <remarks>
/// <c>ResponseCodes</c>. Each is a different sentence in the client, so the wrong one tells a player
/// to fix something that is not the problem.
/// </remarks>
public static class CharCreateResult
{
    public const byte Ok = 0;

    /// <summary>The account already has a character on the other side. <c>CHAR_CREATE_PVP_TEAMS_VIOLATION</c>.</summary>
    public const byte PvpTeamsViolation = 0x34;

    public const byte AccountLimit = 0x36;

    /// <summary>The race needs an expansion the account has not bought.</summary>
    public const byte Expansion = 0x39;

    /// <summary>The class needs one.</summary>
    public const byte ExpansionClass = 0x3A;

    /// <summary>No character on the account is high enough to unlock a death knight.</summary>
    public const byte LevelRequirement = 0x3B;

    /// <summary>The account already has its allowance of death knights on this realm.</summary>
    public const byte UniqueClassLimit = 0x3C;
}

/// <summary>
/// What the account and its existing characters allow this one to be.
/// </summary>
/// <remarks>
/// Port of the gating in <c>WorldSession::HandleCharCreateOpcode</c> and its name-check callback.
/// Pure functions rather than methods on the session: every one of them is a rule about data the
/// client supplied, and the client is not the authority on any of it.
/// </remarks>
public static class CharacterCreationRules
{
    /// <summary>The death knight class id. <c>CLASS_DEATH_KNIGHT</c>.</summary>
    public const byte DeathKnightClass = 6;

    /// <summary>
    /// Whether a race or class is beyond what this account has bought.
    /// </summary>
    /// <returns><see cref="CharCreateResult.Ok"/> to allow.</returns>
    /// <remarks>
    /// Race and class are <b>separate refusals with separate codes</b>, because the client says
    /// different things: one names the race and one names the class.
    /// </remarks>
    /// <param name="allowedExpansion">
    /// The account's, already capped by the realm's. An account entitled to WotLK on a realm
    /// serving TBC is a TBC account here — letting its own figure through would admit a death
    /// knight the realm has no starting zone for.
    /// </param>
    public static byte CheckExpansion(
        byte race,
        byte characterClass,
        byte allowedExpansion,
        DbcStore<ChrRacesEntry>? races,
        DbcStore<ChrClassesEntry>? classes)
    {
        if (races is not null
            && races.TryGet(race, out ChrRacesEntry? raceEntry)
            && raceEntry is not null
            && raceEntry.Expansion > allowedExpansion)
        {
            return CharCreateResult.Expansion;
        }

        return classes is not null
            && classes.TryGet(characterClass, out ChrClassesEntry? classEntry)
            && classEntry is not null
            && classEntry.Expansion > allowedExpansion
                ? CharCreateResult.ExpansionClass
                : CharCreateResult.Ok;
    }

    /// <summary>
    /// Whether the account's existing characters rule this one out.
    /// </summary>
    /// <returns><see cref="CharCreateResult.Ok"/> to allow.</returns>
    /// <remarks>
    /// Two rules, both needing the whole roster rather than a count:
    /// <list type="bullet">
    /// <item><b>Faction.</b> With two-sided accounts off, the new character joins the side the
    /// account is already on. Compared by <i>team</i>, not by race — the several races on a side
    /// are one team, and comparing races refuses a human account a dwarf.</item>
    /// <item><b>Death knights.</b> Unlocked by having got any character on this realm to level 55,
    /// and capped per account. <b>The level is an existing character's, not the new one's</b> —
    /// the new one has no level yet, so reading it refuses every death knight ever.</item>
    /// </list>
    /// </remarks>
    public static byte CheckRoster(
        IReadOnlyList<CharacterSummary> existing,
        byte race,
        byte characterClass,
        bool allowTwoSideAccounts,
        byte minLevelForHeroic,
        byte heroicPerRealm,
        DbcStore<ChrRacesEntry>? races)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (!allowTwoSideAccounts && existing.Count > 0
            && TeamOf(existing[0].Race, races) != TeamOf(race, races))
        {
            // The first character's team is the account's. Upstream leaves an already-mixed account
            // alone with a @todo, and so does this: refusing outright would strand an account made
            // before the rule was turned on.
            return CharCreateResult.PvpTeamsViolation;
        }

        if (characterClass != DeathKnightClass)
        {
            return CharCreateResult.Ok;
        }

        int deathKnights = 0;
        bool unlocked = false;

        foreach (CharacterSummary character in existing)
        {
            if (character.Class == DeathKnightClass)
            {
                deathKnights++;
            }

            if (character.Level >= minLevelForHeroic)
            {
                unlocked = true;
            }
        }

        if (deathKnights >= heroicPerRealm)
        {
            return CharCreateResult.UniqueClassLimit;
        }

        return unlocked ? CharCreateResult.Ok : CharCreateResult.LevelRequirement;
    }

    /// <summary>
    /// Which side a race is on. <c>Player::TeamIdForRace</c>.
    /// </summary>
    /// <remarks>
    /// From <c>ChrRaces.dbc</c> rather than a hard-coded list, so a race the file carries is placed
    /// correctly without anything here changing.
    /// </remarks>
    public static uint TeamOf(byte race, DbcStore<ChrRacesEntry>? races) =>
        races is not null && races.TryGet(race, out ChrRacesEntry? entry) && entry is not null
            ? entry.TeamId
            : 0;
}
