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
    byte Slot);

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
                Slot: pair.row.Slot));
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
    }

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
