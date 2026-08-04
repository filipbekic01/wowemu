using System.Diagnostics.CodeAnalysis;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.WorldServer;

/// <summary>
/// The static data a session needs to turn a saved character into a player.
/// </summary>
/// <remarks>
/// Three sources have to agree before a character can enter the world: the DBC stores say what a
/// race looks like, the world tables say what its stats are at a given level, and the characters
/// row says who it is. Missing data in any of them means the character would render wrongly or not
/// at all, so this refuses rather than logging in something broken.
/// </remarks>
public sealed class WorldContent(DbcStores stores, PlayerStatsStore stats)
{
    public DbcStores Stores { get; } = stores;

    public PlayerStatsStore Stats { get; } = stats;

    /// <summary>
    /// Builds a player, or explains why it cannot.
    /// </summary>
    /// <remarks>
    /// The reason is worth surfacing: "no stats for race 11 class 2 level 1" points straight at a
    /// missing world-table import, whereas a silent failure looks like a networking problem.
    /// </remarks>
    public bool TryBuildPlayer(
        CharacterSummary character,
        [NotNullWhen(true)] out Player? player,
        [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(character);

        player = null;

        if (!Stores.Races.TryGet(character.Race, out ChrRacesEntry? race))
        {
            reason = $"no ChrRaces row for race {character.Race}";
            return false;
        }

        if (!Stores.Classes.TryGet(character.Class, out ChrClassesEntry? characterClass))
        {
            reason = $"no ChrClasses row for class {character.Class}";
            return false;
        }

        if (!Stats.TryGet(character.Race, character.Class, character.Level,
                out LevelStats levelStats, out ClassLevelStats classStats))
        {
            reason =
                $"no base stats for race {character.Race}, class {character.Class}, level {character.Level}";
            return false;
        }

        PlayerBaseStats baseStats = new(
            MaxHealth: classStats.BaseHealth + (levelStats.Stamina * 10),
            MaxMana: classStats.BaseMana + (levelStats.Intellect * 15),
            Strength: levelStats.Strength,
            Agility: levelStats.Agility,
            Stamina: levelStats.Stamina,
            Intellect: levelStats.Intellect,
            Spirit: levelStats.Spirit);

        player = Player.Create(character, race, characterClass, baseStats);
        reason = null;
        return true;
    }
}
