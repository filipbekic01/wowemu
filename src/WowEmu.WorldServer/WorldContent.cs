using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class WorldContent(
    DbcStores stores,
    PlayerStatsStore stats,
    TerrainManager terrain,
    SpellStores spells,
    ItemTemplateStore items,
    CharStartOutfitStore outfits,
    PlayerCreateInfoStore createInfo,
    QuestStore quests,
    [FromKeyedServices("quest_starters")] QuestRelationStore questStarters,
    [FromKeyedServices("quest_enders")] QuestRelationStore questEnders,
    [FromKeyedServices("go_quest_starters")] QuestRelationStore objectQuestStarters,
    [FromKeyedServices("go_quest_enders")] QuestRelationStore objectQuestEnders,
    PlayerXpStore experienceTable,
    GossipStore gossip,
    VendorStore vendors,
    PlayerSpellStore startingSpells,
    PlayerActionStore startingActions,
    TrainerStore trainers,
    SpellRankStore spellRanks)
{
    public TerrainManager Terrain { get; } = terrain;

    /// <summary>Every spell, and the tables its cast time, range and duration index into.</summary>
    public SpellStores Spells { get; } = spells;

    /// <summary>Every item the client can be told about.</summary>
    public ItemTemplateStore Items { get; } = items;

    /// <summary>What each race, class and gender begins with.</summary>
    public CharStartOutfitStore Outfits { get; } = outfits;

    /// <summary>Every quest.</summary>
    public QuestStore Quests { get; } = quests;

    /// <summary>Which creature offers which quest.</summary>
    public QuestRelationStore QuestStarters { get; } = questStarters;

    /// <summary>Which creature takes which quest back. Very often not the same one.</summary>
    public QuestRelationStore QuestEnders { get; } = questEnders;

    /// <summary>Quests that start at an object rather than an NPC.</summary>
    public QuestRelationStore ObjectQuestStarters { get; } = objectQuestStarters;

    /// <summary>Quests handed in at an object rather than an NPC.</summary>
    public QuestRelationStore ObjectQuestEnders { get; } = objectQuestEnders;

    /// <summary>The experience-per-level table, for quest rewards that cross a level.</summary>
    public PlayerXpStore ExperienceTable { get; } = experienceTable;

    /// <summary>What NPCs say, and what can be clicked.</summary>
    public GossipStore Gossip { get; } = gossip;

    /// <summary>What each vendor sells.</summary>
    public VendorStore Vendors { get; } = vendors;

    /// <summary>What each race and class begins able to cast.</summary>
    public PlayerSpellStore StartingSpells { get; } = startingSpells;

    /// <summary>What each trainer teaches.</summary>
    public TrainerStore Trainers { get; } = trainers;

    /// <summary>Which spells are ranks of the same spell, so a higher one supersedes a lower.</summary>
    public SpellRankStore SpellRanks { get; } = spellRanks;

    /// <summary>What is on a new character's action bars.</summary>
    public PlayerActionStore StartingActions { get; } = startingActions;

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

        // Every player built here gets the limit table, so the "one mana gem" family caps apply.
        // A player built anywhere else has none and the category limits simply pass, which is what
        // they did before the table existed.
        player.Inventory.LimitCategories = Stores.ItemLimitCategories;
        player.Spells.Ranks = SpellRanks;

        // The saved zone is where the character logged out; the terrain is the authority on where
        // it actually is. They differ whenever a character was moved by anything but walking.
        ushort area = Terrain.GetMap(player.MapId).GetAreaId(player.Position.X, player.Position.Y);

        if (area != 0)
        {
            player.AreaId = area;
            player.ZoneId = Stores.ZoneFor(area);
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Dresses a brand-new character.
    /// </summary>
    /// <remarks>
    /// Port of the outfit half of <c>Player::Create</c>. Two sources, in upstream's order:
    /// <c>CharStartOutfit.dbc</c> first, then whatever <c>playercreateinfo_item</c> adds on top.
    /// The DBC is the one that matters — the vendored world table has a single row in the whole
    /// database, so reading only it produces a naked character.
    /// <para>
    /// Food and drink get a special count. Everything else takes the template's <c>BuyCount</c>,
    /// which is 1 for almost everything and is the column that makes a starting stack of arrows 200
    /// rather than one.
    /// </para>
    /// </remarks>
    /// <returns>How many distinct items were placed.</returns>
    public int ApplyStartingGear(Player player, Func<uint> nextItemGuid)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(nextItemGuid);

        int placed = 0;

        foreach (uint entry in Outfits.ItemsFor(player.Race, player.Class, player.Gender))
        {
            placed += Give(player, entry, count: 0, nextItemGuid) ? 1 : 0;
        }

        foreach (PlayerCreateItem extra in createInfo.ItemsFor(player.Race, player.Class))
        {
            placed += Give(player, extra.ItemId, extra.Amount, nextItemGuid) ? 1 : 0;
        }

        return placed;
    }

    /// <summary>How many of a starting item to hand over. Zero means "ask the template".</summary>
    private bool Give(Player player, uint entry, uint count, Func<uint> nextItemGuid)
    {
        if (!Items.TryGet(entry, out ItemTemplate? template) || template is null)
        {
            return false;
        }

        uint amount = count > 0 ? count : template.BuyCount;

        // Food and drink are the one exception: a new character gets a meal's worth rather than a
        // single bite. The category is on the item's first spell, not on the item.
        if (template.Class == ItemClass.Consumable && template.SubClass == FoodSubClass)
        {
            amount = template.Spells[0].Category switch
            {
                FoodCategory => 4,
                DrinkCategory => 2,
                _ => amount,
            };

            amount = Math.Min(amount, template.MaxStackSize);
        }

        return player.Inventory.StoreInBestSlots(template, amount, nextItemGuid, out _);
    }

    /// <summary><c>ITEM_SUBCLASS_FOOD</c>, and the two spell categories that separate food from drink.</summary>
    private const byte FoodSubClass = 0;
    private const ushort FoodCategory = 11;
    private const ushort DrinkCategory = 59;
}
