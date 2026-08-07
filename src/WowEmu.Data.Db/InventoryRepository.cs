using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WowEmu.Data.Db;

/// <summary>One stored item, with where it sits.</summary>
/// <param name="BagId">The guid of the containing bag, or zero for the player's own slot array.</param>
public readonly record struct StoredItem(
    uint ItemId,
    uint Entry,
    uint Count,
    uint Durability,
    uint DurationSeconds,
    int[] SpellCharges,
    uint Flags,
    uint BagId,
    byte Slot,

    /// <summary>The rolled random-properties id, signed, and the factor its amounts scale from.</summary>
    int RandomPropertyId = 0,
    uint SuffixFactor = 0);

/// <summary>One stored talent: which spec, which talent, at what rank.</summary>
public readonly record struct StoredTalent(byte Spec, uint TalentId, byte Rank);

/// <summary>One stored glyph socket.</summary>
public readonly record struct StoredGlyph(byte Spec, byte Slot, uint GlyphId);

/// <summary>One stored quest.</summary>
public readonly record struct StoredQuest(uint QuestId, byte Status, byte Slot, ushort[] Killed);

/// <summary>Reads and writes what characters are carrying.</summary>
public interface IInventoryRepository
{
    /// <summary>
    /// Everything a character holds, bags before their contents.
    /// </summary>
    /// <remarks>
    /// The order matters: a bag's contents cannot be placed until the bag itself is, so rows in the
    /// player's own array come first. Sorting in the database rather than the caller keeps the
    /// dependency in one place.
    /// </remarks>
    Task<IReadOnlyList<StoredItem>> LoadAsync(uint characterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces everything a character holds with <paramref name="items"/>.
    /// </summary>
    /// <remarks>
    /// Whole-inventory rather than per-item, because there is no change tracking on the game side:
    /// an item can move, stack, split or vanish within one tick, and reconciling that would mean
    /// duplicating the inventory's own bookkeeping. Sixteen to eighty rows per save is cheap, and
    /// it cannot leave a stale row behind — which is the failure that duplicates items.
    /// </remarks>
    Task SaveAsync(
        uint characterId,
        IReadOnlyList<StoredItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes everything a character holds. Part of deleting the character.</summary>
    Task DeleteForCharacterAsync(uint characterId, CancellationToken cancellationToken = default);

    /// <summary>Every quest a character has taken, handed-in ones included.</summary>
    Task<IReadOnlyList<StoredQuest>> LoadQuestsAsync(uint characterId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SaveAsync"/>
    Task SaveQuestsAsync(
        uint characterId,
        IReadOnlyList<StoredQuest> quests,
        CancellationToken cancellationToken = default);

    /// <summary>Every spell a character knows.</summary>
    Task<IReadOnlyList<uint>> LoadSpellsAsync(uint characterId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SaveAsync"/>
    Task SaveSpellsAsync(
        uint characterId,
        IReadOnlyCollection<uint> spells,
        CancellationToken cancellationToken = default);

    /// <summary>What a character is trained in, as (skill, value, max, step) rows.</summary>
    Task<IReadOnlyList<(ushort Skill, ushort Value, ushort Max, ushort Step)>> LoadSkillsAsync(
        uint characterId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SaveAsync"/>
    Task SaveSkillsAsync(
        uint characterId,
        IReadOnlyCollection<(ushort Skill, ushort Value, ushort Max, ushort Step)> skills,
        CancellationToken cancellationToken = default);

    /// <summary>What every faction this character has met thinks of them.</summary>
    Task<IReadOnlyList<(ushort Faction, int Standing)>> LoadReputationAsync(
        uint characterId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SaveAsync"/>
    Task SaveReputationAsync(
        uint characterId,
        IReadOnlyCollection<(ushort Faction, int Standing)> reputation,
        CancellationToken cancellationToken = default);

    /// <summary>A character's talents and glyphs, and which spec is active.</summary>
    Task<(byte ActiveSpec, byte SpecCount, IReadOnlyList<StoredTalent> Talents,
        IReadOnlyList<StoredGlyph> Glyphs)>
        LoadTalentsAsync(uint characterId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SaveAsync"/>
    Task SaveTalentsAsync(
        uint characterId,
        IReadOnlyCollection<StoredTalent> talents,
        IReadOnlyCollection<StoredGlyph> glyphs,
        CancellationToken cancellationToken = default);

    /// <summary>Which repeating quests a character has done, by period.</summary>
    Task<(IReadOnlyList<uint> Daily, IReadOnlyList<uint> Weekly, IReadOnlyList<uint> Monthly)>
        LoadQuestResetsAsync(uint characterId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SaveAsync"/>
    Task SaveQuestResetsAsync(
        uint characterId,
        IReadOnlyCollection<uint> daily,
        IReadOnlyCollection<uint> weekly,
        IReadOnlyCollection<uint> monthly,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears one period's record for every character on the server.
    /// </summary>
    /// <remarks>
    /// <b>Every character, not just the ones logged in.</b> The reset is a server-wide instant, and
    /// clearing only online characters leaves everyone else holding yesterday's record until they
    /// next log out — which is to say, indefinitely.
    /// </remarks>
    Task ResetAllQuestsAsync(QuestResetPeriod period, CancellationToken cancellationToken = default);

    /// <summary>Where a character comes back to, or null when they have never bound anywhere.</summary>
    Task<(uint MapId, uint AreaId, float X, float Y, float Z)?> LoadHomebindAsync(
        uint characterId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SaveAsync"/>
    Task SaveHomebindAsync(
        uint characterId,
        uint mapId,
        uint areaId,
        float x,
        float y,
        float z,
        CancellationToken cancellationToken = default);

    /// <summary>What is on a character's action bars, as (button, packed action) pairs.</summary>
    Task<IReadOnlyList<(byte Button, uint Packed)>> LoadActionsAsync(
        uint characterId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="SaveAsync"/>
    Task SaveActionsAsync(
        uint characterId,
        IReadOnlyDictionary<byte, uint> buttons,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The highest item guid in use, so the in-memory allocator can carry on above it.
    /// </summary>
    /// <remarks>
    /// Reissuing a guid the database already holds is the worst failure this table has: two items
    /// share an identity and one overwrites the other on the next save.
    /// </remarks>
    Task<uint> HighestItemIdAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IInventoryRepository"/>
public sealed class InventoryRepository(IDbContextFactory<CharactersDbContext> contextFactory)
    : IInventoryRepository
{
    public async Task<IReadOnlyList<StoredItem>> LoadAsync(
        uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await context.Inventory
            .AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .Join(
                context.Items.AsNoTracking(),
                row => row.ItemId,
                item => item.Id,
                (row, item) => new { row, item })

            // Bags first — a row inside a bag cannot be placed before the bag exists.
            .OrderBy(pair => pair.row.BagId)
            .ThenBy(pair => pair.row.Slot)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<StoredItem> loaded = new(rows.Count);

        foreach (var pair in rows)
        {
            loaded.Add(new StoredItem(
                ItemId: pair.item.Id,
                Entry: pair.item.Entry,
                Count: pair.item.Count,
                Durability: pair.item.Durability,
                DurationSeconds: pair.item.DurationSeconds,
                SpellCharges: ParseCharges(pair.item.SpellCharges),
                Flags: pair.item.Flags,
                BagId: pair.row.BagId,
                Slot: pair.row.Slot,
                RandomPropertyId: pair.item.RandomPropertyId,
                SuffixFactor: pair.item.SuffixFactor));
        }

        return loaded;
    }

    public async Task SaveAsync(
        uint characterId,
        IReadOnlyList<StoredItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // The placements go first and wholesale: an item that moved has a stale row, and an item
        // that was destroyed has one with nothing behind it.
        await context.Inventory
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // Instances belonging to this character but no longer placed anywhere are gone. Scoped by
        // owner rather than by id list, so a destroyed item cannot linger as an orphan.
        await context.Items
            .Where(item => item.OwnerId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (StoredItem item in items)
        {
            context.Items.Add(new ItemInstanceEntity
            {
                Id = item.ItemId,
                Entry = item.Entry,
                OwnerId = characterId,
                Count = item.Count,
                Durability = item.Durability,
                DurationSeconds = item.DurationSeconds,
                SpellCharges = FormatCharges(item.SpellCharges),
                Flags = item.Flags,
                RandomPropertyId = item.RandomPropertyId,
                SuffixFactor = item.SuffixFactor,
            });

            context.Inventory.Add(new CharacterInventoryEntity
            {
                ItemId = item.ItemId,
                CharacterId = characterId,
                BagId = item.BagId,
                Slot = item.Slot,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteForCharacterAsync(uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await context.Inventory
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await context.Items
            .Where(item => item.OwnerId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await context.Quests
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await context.Spells
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await context.Actions
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredQuest>> LoadQuestsAsync(
        uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<CharacterQuestEntity> rows = await context.Quests
            .AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<StoredQuest> loaded = new(rows.Count);

        foreach (CharacterQuestEntity row in rows)
        {
            loaded.Add(new StoredQuest(
                row.QuestId,
                row.Status,
                row.Slot,
                [row.Killed1, row.Killed2, row.Killed3, row.Killed4]));
        }

        return loaded;
    }

    public async Task SaveQuestsAsync(
        uint characterId,
        IReadOnlyList<StoredQuest> quests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quests);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Whole-set, for the same reason the inventory is: an abandoned quest has no row to update
        // and leaving a stale one would put it back in the log on the next login.
        await context.Quests
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (StoredQuest quest in quests)
        {
            context.Quests.Add(new CharacterQuestEntity
            {
                CharacterId = characterId,
                QuestId = quest.QuestId,
                Status = quest.Status,
                Slot = quest.Slot,
                Killed1 = At(quest.Killed, 0),
                Killed2 = At(quest.Killed, 1),
                Killed3 = At(quest.Killed, 2),
                Killed4 = At(quest.Killed, 3),
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<uint>> LoadSpellsAsync(
        uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Spells
            .AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .Select(row => row.SpellId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveSpellsAsync(
        uint characterId,
        IReadOnlyCollection<uint> spells,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spells);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Whole-set, like the inventory and the quest log: a spell unlearned has no row to update.
        await context.Spells
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (uint spellId in spells)
        {
            context.Spells.Add(new CharacterSpellEntity { CharacterId = characterId, SpellId = spellId });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(uint MapId, uint AreaId, float X, float Y, float Z)?> LoadHomebindAsync(
        uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CharacterHomebindEntity? row = await context.Homebinds
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.CharacterId == characterId, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : (row.MapId, row.AreaId, row.PositionX, row.PositionY, row.PositionZ);
    }

    public async Task SaveHomebindAsync(
        uint characterId,
        uint mapId,
        uint areaId,
        float x,
        float y,
        float z,
        CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CharacterHomebindEntity? row = await context.Homebinds
            .SingleOrDefaultAsync(entity => entity.CharacterId == characterId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new CharacterHomebindEntity { CharacterId = characterId };
            context.Homebinds.Add(row);
        }

        row.MapId = mapId;
        row.AreaId = areaId;
        row.PositionX = x;
        row.PositionY = y;
        row.PositionZ = z;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(ushort Skill, ushort Value, ushort Max, ushort Step)>> LoadSkillsAsync(
        uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<CharacterSkillEntity> rows = await context.Skills
            .AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => (row.SkillId, row.Value, row.MaxValue, row.Step))];
    }

    public async Task SaveSkillsAsync(
        uint characterId,
        IReadOnlyCollection<(ushort Skill, ushort Value, ushort Max, ushort Step)> skills,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skills);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Whole-set, like the spells: a skill forgotten has no row to update.
        await context.Skills
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach ((ushort skill, ushort value, ushort max, ushort step) in skills)
        {
            context.Skills.Add(new CharacterSkillEntity
            {
                CharacterId = characterId,
                SkillId = skill,
                Value = value,
                MaxValue = max,
                Step = step,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(ushort Faction, int Standing)>> LoadReputationAsync(
        uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<CharacterReputationEntity> rows = await context.Reputation
            .AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => (row.FactionId, row.Standing))];
    }

    public async Task SaveReputationAsync(
        uint characterId,
        IReadOnlyCollection<(ushort Faction, int Standing)> reputation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reputation);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Whole-set, like the skills.
        await context.Reputation
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach ((ushort faction, int standing) in reputation)
        {
            context.Reputation.Add(new CharacterReputationEntity
            {
                CharacterId = characterId,
                FactionId = faction,
                Standing = standing,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(byte ActiveSpec, byte SpecCount, IReadOnlyList<StoredTalent> Talents,
        IReadOnlyList<StoredGlyph> Glyphs)>
        LoadTalentsAsync(uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<StoredTalent> talents = await context.Talents.AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .Select(row => new StoredTalent(row.Spec, row.TalentId, row.Rank))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<StoredGlyph> glyphs = await context.Glyphs.AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .Select(row => new StoredGlyph(row.Spec, row.Slot, row.GlyphId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var specs = await context.Characters.AsNoTracking()
            .Where(character => character.Id == characterId)
            .Select(character => new { character.ActiveSpec, character.SpecCount })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return (specs?.ActiveSpec ?? 0, specs?.SpecCount ?? 1, talents, glyphs);
    }

    public async Task SaveTalentsAsync(
        uint characterId,
        IReadOnlyCollection<StoredTalent> talents,
        IReadOnlyCollection<StoredGlyph> glyphs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(talents);
        ArgumentNullException.ThrowIfNull(glyphs);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Whole-set, like the skills: a talent reset leaves nothing to update.
        await context.Talents.Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.Glyphs.Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        foreach (StoredTalent talent in talents)
        {
            context.Talents.Add(new CharacterTalentEntity
            {
                CharacterId = characterId,
                Spec = talent.Spec,
                TalentId = talent.TalentId,
                Rank = talent.Rank,
            });
        }

        foreach (StoredGlyph glyph in glyphs)
        {
            // An empty socket is the absence of a row, not a row of zero — otherwise every
            // character carries twelve rows saying nothing.
            if (glyph.GlyphId == 0)
            {
                continue;
            }

            context.Glyphs.Add(new CharacterGlyphEntity
            {
                CharacterId = characterId,
                Spec = glyph.Spec,
                Slot = glyph.Slot,
                GlyphId = glyph.GlyphId,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<uint> Daily, IReadOnlyList<uint> Weekly, IReadOnlyList<uint> Monthly)>
        LoadQuestResetsAsync(uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<uint> daily = await context.DailyQuests.AsNoTracking()
            .Where(row => row.CharacterId == characterId).Select(row => row.QuestId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<uint> weekly = await context.WeeklyQuests.AsNoTracking()
            .Where(row => row.CharacterId == characterId).Select(row => row.QuestId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<uint> monthly = await context.MonthlyQuests.AsNoTracking()
            .Where(row => row.CharacterId == characterId).Select(row => row.QuestId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return (daily, weekly, monthly);
    }

    public async Task SaveQuestResetsAsync(
        uint characterId,
        IReadOnlyCollection<uint> daily,
        IReadOnlyCollection<uint> weekly,
        IReadOnlyCollection<uint> monthly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(daily);
        ArgumentNullException.ThrowIfNull(weekly);
        ArgumentNullException.ThrowIfNull(monthly);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Whole-set per period, like the skills.
        await context.DailyQuests.Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.WeeklyQuests.Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.MonthlyQuests.Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        foreach (uint questId in daily)
        {
            context.DailyQuests.Add(new CharacterQuestDailyEntity
            {
                CharacterId = characterId,
                QuestId = questId,
            });
        }

        foreach (uint questId in weekly)
        {
            context.WeeklyQuests.Add(new CharacterQuestWeeklyEntity
            {
                CharacterId = characterId,
                QuestId = questId,
            });
        }

        foreach (uint questId in monthly)
        {
            context.MonthlyQuests.Add(new CharacterQuestMonthlyEntity
            {
                CharacterId = characterId,
                QuestId = questId,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAllQuestsAsync(
        QuestResetPeriod period, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        switch (period)
        {
            case QuestResetPeriod.Daily:
                await context.DailyQuests.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                break;

            case QuestResetPeriod.Weekly:
                await context.WeeklyQuests.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                break;

            case QuestResetPeriod.Monthly:
                await context.MonthlyQuests.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(period));
        }
    }

    public async Task<IReadOnlyList<(byte Button, uint Packed)>> LoadActionsAsync(
        uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<CharacterActionEntity> rows = await context.Actions
            .AsNoTracking()
            .Where(row => row.CharacterId == characterId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<(byte, uint)> loaded = new(rows.Count);

        foreach (CharacterActionEntity row in rows)
        {
            // Repacked on the way out: the two are stored apart so a query can read either.
            loaded.Add((row.Button, (row.Action & 0x00FFFFFF) | ((uint)row.Type << 24)));
        }

        return loaded;
    }

    public async Task SaveActionsAsync(
        uint characterId,
        IReadOnlyDictionary<byte, uint> buttons,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buttons);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await context.Actions
            .Where(row => row.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach ((byte button, uint packed) in buttons)
        {
            context.Actions.Add(new CharacterActionEntity
            {
                CharacterId = characterId,
                Button = button,
                Action = packed & 0x00FFFFFF,
                Type = (byte)(packed >> 24),
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ushort At(ushort[]? counters, int index) =>
        counters is not null && index < counters.Length ? counters[index] : (ushort)0;

    public async Task<uint> HighestItemIdAsync(CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // MaxAsync on an empty table throws for a non-nullable projection, so it is widened first.
        return await context.Items
            .AsNoTracking()
            .Select(item => (uint?)item.Id)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;
    }

    private static int[] ParseCharges(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return [];
        }

        string[] parts = stored.Split(',', StringSplitOptions.RemoveEmptyEntries);
        int[] charges = new int[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            charges[i] = int.TryParse(parts[i], System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
        }

        return charges;
    }

    private static string FormatCharges(int[]? charges)
    {
        if (charges is null || charges.Length == 0)
        {
            return string.Empty;
        }

        // An item with no charges at all is the common case by a wide margin, and writing "0,0,0,0,0"
        // for every one of them is a third of the row for nothing.
        bool anything = false;

        foreach (int charge in charges)
        {
            if (charge != 0)
            {
                anything = true;
                break;
            }
        }

        return anything
            ? string.Join(',', charges)
            : string.Empty;
    }
}

/// <summary>
/// Hands out item guids that nothing else is using.
/// </summary>
/// <remarks>
/// Seeded once at startup from the highest guid in <c>item_instance</c> and never consulted again —
/// the world server is the only writer, so the in-memory counter is authoritative for its lifetime.
/// <para>
/// <b>Guids are not reused.</b> A destroyed item's number is retired rather than filled in, because
/// something may still be holding it: a client's cache, a pending save, a loot window. 32 bits at a
/// few thousand items a day is centuries.
/// </para>
/// </remarks>
public sealed class ItemGuidGenerator
{
    private int _next;

    /// <summary>The last guid handed out. Zero before the first.</summary>
    public uint Last => (uint)Volatile.Read(ref _next);

    /// <summary>Starts the counter above everything already stored.</summary>
    public void SeedFrom(uint highestInUse) => Volatile.Write(ref _next, (int)highestInUse);

    /// <summary>
    /// The next unused guid counter.
    /// </summary>
    /// <remarks>
    /// Interlocked despite the world server being single-threaded here, because startup and the
    /// tick are not the same thread and a seed racing a first allocation would be silent.
    /// </remarks>
    public uint Next() => (uint)Interlocked.Increment(ref _next);
}

/// <summary>Registers the inventory repository and the guid allocator.</summary>
public static class InventoryDatabase
{
    public static IServiceCollection AddInventoryDatabase(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IInventoryRepository, InventoryRepository>();
        services.AddSingleton<ItemGuidGenerator>();

        return services;
    }
}
