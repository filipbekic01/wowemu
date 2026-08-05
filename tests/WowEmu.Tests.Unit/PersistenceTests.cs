using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WowEmu.Data.Db;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Whether the characters database is reachable and migrated.
/// </summary>
/// <remarks>
/// A separate probe from <see cref="WorldDatabase"/>: the two are different schemas with different
/// lifecycles, and the world one can be seeded while this has never been migrated.
/// </remarks>
internal static class CharacterDatabaseProbe
{
    private static readonly Lazy<bool> Probe = new(() =>
    {
        try
        {
            using MySqlConnection connection = new(ConnectionString);
            connection.Open();

            using MySqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM character_queststatus";

            command.ExecuteScalar();

            return true;
        }
        catch (MySqlException)
        {
            // Not running, or the migration has never been applied. Both mean the same here.
            return false;
        }
    });

    public static string ConnectionString => CharacterDatabase.ResolveConnectionString();

    public static bool Available => Probe.Value;
}

/// <summary>Marks a fact that needs a migrated characters database.</summary>
public sealed class RequiresCharacterDatabaseFactAttribute : FactAttribute
{
    public RequiresCharacterDatabaseFactAttribute()
    {
        if (!CharacterDatabaseProbe.Available)
        {
            Skip = "The characters database is not reachable or has not been migrated. "
                 + "Start the world server once to apply migrations.";
        }
    }
}

/// <summary>
/// What survives a logout, against the real database.
/// </summary>
/// <remarks>
/// The one thing the headless gates cannot easily reach. A gate can log a character out and back
/// in, but it cannot see the rows — and a save that silently writes nothing looks identical to one
/// that works until the next login.
/// <para>
/// Every test here uses a character id far above anything a real account would produce, and clears
/// it before and after. A test that leaves rows behind makes the next run pass for the wrong
/// reason.
/// </para>
/// </remarks>
public sealed class InventoryPersistenceTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>Well past any real character. Each test class uses its own so they can run in parallel.</summary>
    private const uint TestCharacter = 0x7F00_0001;

    private static CancellationToken TestToken => CancellationToken.None;

    private InventoryRepository? _repository;

    public async Task InitializeAsync()
    {
        if (!CharacterDatabaseProbe.Available)
        {
            return;
        }

        _repository = new InventoryRepository(new TestContextFactory());

        await _repository.DeleteForCharacterAsync(TestCharacter, TestToken);
    }

    public async Task DisposeAsync()
    {
        if (_repository is not null)
        {
            await _repository.DeleteForCharacterAsync(TestCharacter, TestToken);
        }
    }

    /// <summary>An inventory written out comes back the same.</summary>
    [RequiresCharacterDatabaseFact]
    public async Task AnInventory_SurvivesARoundTrip()
    {
        StoredItem[] saved =
        [
            new StoredItem(9001, 25, 1, 55, 0, [0, 0, 0, 0, 0], 0, BagId: 0, Slot: 15),
            new StoredItem(9002, 2589, 17, 0, 0, [0, 0, 0, 0, 0], 0, BagId: 0, Slot: 23),
        ];

        await _repository!.SaveAsync(TestCharacter, saved, TestToken);

        IReadOnlyList<StoredItem> loaded = await _repository.LoadAsync(TestCharacter, TestToken);

        Assert.Equal(2, loaded.Count);

        StoredItem sword = loaded.First(item => item.ItemId == 9001);

        Assert.Equal(25u, sword.Entry);
        Assert.Equal(1u, sword.Count);
        Assert.Equal(55u, sword.Durability);
        Assert.Equal(15, sword.Slot);

        Assert.Equal(17u, loaded.First(item => item.ItemId == 9002).Count);

        output.WriteLine($"round-tripped {loaded.Count} items");
    }

    /// <summary>
    /// Saving replaces, so an item that moved does not end up in two places.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is the one that duplicates items: a stale placement row left
    /// behind alongside the new one, and the item in both slots on the next login.
    /// </remarks>
    [RequiresCharacterDatabaseFact]
    public async Task SavingReplaces_SoAMovedItemIsNotInTwoPlaces()
    {
        await _repository!.SaveAsync(
            TestCharacter,
            [new StoredItem(9010, 25, 1, 0, 0, [], 0, BagId: 0, Slot: 23)],
            TestToken);

        await _repository.SaveAsync(
            TestCharacter,
            [new StoredItem(9010, 25, 1, 0, 0, [], 0, BagId: 0, Slot: 30)],
            TestToken);

        IReadOnlyList<StoredItem> loaded = await _repository.LoadAsync(TestCharacter, TestToken);

        Assert.Single(loaded);
        Assert.Equal(30, loaded[0].Slot);
    }

    /// <summary>An item destroyed between saves is gone, not orphaned.</summary>
    [RequiresCharacterDatabaseFact]
    public async Task ADestroyedItem_LeavesNoOrphan()
    {
        await _repository!.SaveAsync(
            TestCharacter,
            [
                new StoredItem(9020, 25, 1, 0, 0, [], 0, 0, 23),
                new StoredItem(9021, 2589, 5, 0, 0, [], 0, 0, 24),
            ],
            TestToken);

        await _repository.SaveAsync(
            TestCharacter, [new StoredItem(9020, 25, 1, 0, 0, [], 0, 0, 23)], TestToken);

        Assert.Single(await _repository.LoadAsync(TestCharacter, TestToken));
    }

    /// <summary>
    /// A bag's contents come back after the bag, so there is somewhere to put them.
    /// </summary>
    /// <remarks>
    /// The ordering is the database's job precisely so the caller cannot get it wrong. Loading a
    /// bag's contents first drops them: the bag does not exist yet, and the slot is refused.
    /// </remarks>
    [RequiresCharacterDatabaseFact]
    public async Task ABagsContents_ComeBackAfterTheBag()
    {
        await _repository!.SaveAsync(
            TestCharacter,
            [
                new StoredItem(9031, 2589, 3, 0, 0, [], 0, BagId: 9030, Slot: 0),
                new StoredItem(9030, 4496, 1, 0, 0, [], 0, BagId: 0, Slot: 19),
            ],
            TestToken);

        IReadOnlyList<StoredItem> loaded = await _repository.LoadAsync(TestCharacter, TestToken);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0u, loaded[0].BagId);
        Assert.Equal(9030u, loaded[1].BagId);
    }

    /// <summary>
    /// Spell charges round-trip with their sign, and an item with none stores nothing.
    /// </summary>
    /// <remarks>
    /// A negative count is what destroys the item when it runs out. Storing the absolute value
    /// leaves an empty potion in the bag forever.
    /// </remarks>
    [RequiresCharacterDatabaseFact]
    public async Task SpellCharges_KeepTheirSign()
    {
        await _repository!.SaveAsync(
            TestCharacter,
            [
                new StoredItem(9040, 118, 1, 0, 0, [-1, 0, 0, 0, 0], 0, 0, 23),
                new StoredItem(9041, 25, 1, 0, 0, [0, 0, 0, 0, 0], 0, 0, 24),
            ],
            TestToken);

        IReadOnlyList<StoredItem> loaded = await _repository.LoadAsync(TestCharacter, TestToken);

        StoredItem potion = loaded.First(item => item.ItemId == 9040);
        StoredItem sword = loaded.First(item => item.ItemId == 9041);

        Assert.Equal(-1, potion.SpellCharges[0]);

        // An item with nothing but zeroes writes an empty column rather than "0,0,0,0,0".
        Assert.Empty(sword.SpellCharges);
    }

    /// <summary>The item guid allocator starts above whatever is already stored.</summary>
    /// <remarks>
    /// Reissuing a guid makes two items share an identity, and one overwrites the other on the next
    /// save — silently, and only for whoever logged out second.
    /// </remarks>
    [RequiresCharacterDatabaseFact]
    public async Task TheGuidAllocator_StartsAboveWhatIsStored()
    {
        await _repository!.SaveAsync(
            TestCharacter, [new StoredItem(9050, 25, 1, 0, 0, [], 0, 0, 23)], TestToken);

        uint highest = await _repository.HighestItemIdAsync(TestToken);

        Assert.True(highest >= 9050, $"the highest guid came back as {highest}");

        ItemGuidGenerator generator = new();
        generator.SeedFrom(highest);

        Assert.Equal(highest + 1, generator.Next());
    }

    private sealed class TestContextFactory : IDbContextFactory<CharactersDbContext>
    {
        public CharactersDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<CharactersDbContext> options = new();
            options.UseMySQL(CharacterDatabaseProbe.ConnectionString);

            return new CharactersDbContext(options.Options);
        }
    }
}

/// <summary>What a quest log looks like after a logout.</summary>
public sealed class QuestPersistenceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const uint TestCharacter = 0x7F00_0002;

    private static CancellationToken TestToken => CancellationToken.None;

    private InventoryRepository? _repository;

    public async Task InitializeAsync()
    {
        if (!CharacterDatabaseProbe.Available)
        {
            return;
        }

        _repository = new InventoryRepository(new TestContextFactory());

        await _repository.DeleteForCharacterAsync(TestCharacter, TestToken);
    }

    public async Task DisposeAsync()
    {
        if (_repository is not null)
        {
            await _repository.DeleteForCharacterAsync(TestCharacter, TestToken);
        }
    }

    /// <summary>
    /// A part-finished quest comes back with its counters.
    /// </summary>
    /// <remarks>
    /// The four <c>mobcount</c> columns are the only quest progress that is actually stored — item
    /// objectives are recounted from the bags — so this is the one place a lost kill would show.
    /// </remarks>
    [RequiresCharacterDatabaseFact]
    public async Task APartFinishedQuest_KeepsItsCounters()
    {
        await _repository!.SaveQuestsAsync(
            TestCharacter,
            [new StoredQuest(7, (byte)QuestStatus.Incomplete, Slot: 3, Killed: [5, 0, 2, 0])],
            TestToken);

        IReadOnlyList<StoredQuest> loaded = await _repository.LoadQuestsAsync(TestCharacter, TestToken);

        Assert.Single(loaded);
        Assert.Equal(7u, loaded[0].QuestId);
        Assert.Equal((byte)QuestStatus.Incomplete, loaded[0].Status);
        Assert.Equal(3, loaded[0].Slot);
        Assert.Equal([(ushort)5, 0, 2, 0], loaded[0].Killed);

        output.WriteLine($"quest {loaded[0].QuestId} came back at slot {loaded[0].Slot}");
    }

    /// <summary>
    /// A handed-in quest keeps its row, out of the log.
    /// </summary>
    /// <remarks>
    /// That row is what stops the quest being offered again. Dropping it lets a player repeat every
    /// quest in the game by logging out and talking to the same NPC.
    /// </remarks>
    [RequiresCharacterDatabaseFact]
    public async Task ARewardedQuest_KeepsItsRowOutOfTheLog()
    {
        await _repository!.SaveQuestsAsync(
            TestCharacter,
            [new StoredQuest(5261, (byte)QuestStatus.Rewarded, Slot: 255, Killed: [0, 0, 0, 0])],
            TestToken);

        IReadOnlyList<StoredQuest> loaded = await _repository.LoadQuestsAsync(TestCharacter, TestToken);

        Assert.Single(loaded);
        Assert.Equal((byte)QuestStatus.Rewarded, loaded[0].Status);
        Assert.Equal(255, loaded[0].Slot);
    }

    /// <summary>An abandoned quest leaves nothing behind.</summary>
    /// <remarks>
    /// A stale row puts it straight back in the log on the next login, which is why the save
    /// replaces the whole set rather than updating what it knows about.
    /// </remarks>
    [RequiresCharacterDatabaseFact]
    public async Task AnAbandonedQuest_LeavesNothingBehind()
    {
        await _repository!.SaveQuestsAsync(
            TestCharacter,
            [
                new StoredQuest(7, (byte)QuestStatus.Incomplete, 0, [1, 0, 0, 0]),
                new StoredQuest(33, (byte)QuestStatus.Incomplete, 1, [0, 0, 0, 0]),
            ],
            TestToken);

        await _repository.SaveQuestsAsync(
            TestCharacter,
            [new StoredQuest(7, (byte)QuestStatus.Incomplete, 0, [1, 0, 0, 0])],
            TestToken);

        IReadOnlyList<StoredQuest> loaded = await _repository.LoadQuestsAsync(TestCharacter, TestToken);

        Assert.Single(loaded);
        Assert.Equal(7u, loaded[0].QuestId);
    }

    /// <summary>Deleting a character takes its quests with it, along with its things.</summary>
    [RequiresCharacterDatabaseFact]
    public async Task DeletingACharacter_TakesItsQuests()
    {
        await _repository!.SaveQuestsAsync(
            TestCharacter,
            [new StoredQuest(7, (byte)QuestStatus.Incomplete, 0, [1, 0, 0, 0])],
            TestToken);

        await _repository.SaveAsync(
            TestCharacter, [new StoredItem(9060, 25, 1, 0, 0, [], 0, 0, 23)], TestToken);

        await _repository.DeleteForCharacterAsync(TestCharacter, TestToken);

        Assert.Empty(await _repository.LoadQuestsAsync(TestCharacter, TestToken));
        Assert.Empty(await _repository.LoadAsync(TestCharacter, TestToken));
    }

    private sealed class TestContextFactory : IDbContextFactory<CharactersDbContext>
    {
        public CharactersDbContext CreateDbContext()
        {
            DbContextOptionsBuilder<CharactersDbContext> options = new();
            options.UseMySQL(CharacterDatabaseProbe.ConnectionString);

            return new CharactersDbContext(options.Options);
        }
    }
}
