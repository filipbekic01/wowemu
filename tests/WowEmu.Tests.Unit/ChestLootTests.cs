using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Opening a chest.
/// </summary>
/// <remarks>
/// 38,481 of the 38,594 spawned chests are locked, so this is mostly a test of the lock standing
/// between a player and the loot table.
/// </remarks>
public sealed class ChestLootTests
{
    /// <summary>An unlocked chest opens and shows what is in it.</summary>
    [Fact]
    public void AnUnlockedChest_Opens()
    {
        (Map map, Player player, GameObject chest, MapCombatFixture.Link link) = World(lockId: 0);

        Assert.True(map.OpenChest(player, chest));
        Assert.NotNull(chest.Loot);
        Assert.Equal(chest.Guid, player.LootTarget);
        Assert.NotEmpty(link.LootWindows);
    }

    /// <summary>A locked one does not, and says so.</summary>
    [Fact]
    public void ALockedChest_IsRefused()
    {
        (Map map, Player player, GameObject chest, MapCombatFixture.Link link) = World(lockId: SkillLock);

        Assert.False(map.OpenChest(player, chest));
        Assert.Null(chest.Loot);
        Assert.Contains(link.LootErrors, error => error == LootError.Locked);
    }

    /// <summary>And opens once the character can pick it.</summary>
    [Fact]
    public void ALockedChest_OpensWithTheSkill()
    {
        (Map map, Player player, GameObject chest, _) = World(lockId: SkillLock);

        player.Skills.Set(SkillType.Lockpicking, 0, 100, 300);

        Assert.True(map.OpenChest(player, chest));
    }

    /// <summary>
    /// The loot is rolled once and kept, not re-rolled per opener.
    /// </summary>
    /// <remarks>
    /// Rolling per player turns one chest into one chest each, which is the difference between a
    /// shared world object and a personal one.
    /// </remarks>
    [Fact]
    public void TheLoot_IsRolledOnceAndKept()
    {
        (Map map, Player player, GameObject chest, _) = World(lockId: 0);

        Assert.True(map.OpenChest(player, chest));

        Loot first = Assert.IsType<Loot>(chest.Loot);

        Assert.True(map.OpenChest(player, chest));

        Assert.Same(first, chest.Loot);
    }

    /// <summary>Something out of reach is refused.</summary>
    [Fact]
    public void AChestOutOfReach_IsRefused()
    {
        (Map map, Player player, GameObject chest, MapCombatFixture.Link link) = World(lockId: 0);

        map.Relocate(player, new Position(500f, 500f, 0f, 0f));

        Assert.False(map.OpenChest(player, chest));
        Assert.Contains(link.LootErrors, error => error == LootError.TooFar);
    }

    /// <summary>Something that is not a chest is not opened as one.</summary>
    [Fact]
    public void SomethingThatIsNotAChest_IsNotOpened()
    {
        (Map map, Player player, _, _) = World(lockId: 0);

        GameObject door = Object(entry: 99, type: DoorType, lockId: 0);

        Assert.False(map.OpenChest(player, door));
    }

    private const uint SkillLock = 2;
    private const byte DoorType = 0;
    private const uint ChestLootId = 700;
    private const uint TreasureEntry = 5001;

    private static (Map Map, Player Player, GameObject Chest, MapCombatFixture.Link Link) World(uint lockId)
    {
        (Map map, Player player, _, MapCombatFixture.Link link) = MapCombatFixture.Engaged(
            items: LootFixture.Items(ItemFixture.Build(entry: TreasureEntry, name: "Treasure")),
            lootReferences: LootFixture.References(1, LootFixture.Template()),
            gameObjectLoot: ChestTable(),
            locks: LockTable());

        GameObject chest = Object(entry: 500, type: GameObjectTemplate.TypeChest, lockId: lockId);

        map.Add(chest);

        return (map, player, chest, link);
    }

    private static GameObject Object(uint entry, byte type, uint lockId)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[0] = lockId;
        data[1] = ChestLootId;

        GameObjectTemplate template = new(
            entry, type, DisplayId: 1, Name: "Chest", Faction: 0, Flags: 0, Size: 1f, Data: data);

        GameObjectSpawn spawn = new(
            SpawnId: entry, Entry: entry, MapId: 0, SpawnMask: 1, PhaseMask: 1,
            Position: new Position(1f, 0f, 0f, 0f),
            Rotation0: 0f, Rotation1: 0f, Rotation2: 0f, Rotation3: 0f,
            State: 1, AnimProgress: 100);

        return GameObject.Create(spawn, template);
    }

    private static LootStore ChestTable() =>
        LootFixture.Store(
            "gameobject_loot_template",
            ChestLootId,
            LootFixture.Template(LootFixture.Row(itemId: TreasureEntry, chance: 100f)));

    private static DbcStore<LockEntry> LockTable()
    {
        uint[] types = new uint[LockEntry.Cases];
        uint[] indices = new uint[LockEntry.Cases];
        uint[] skills = new uint[LockEntry.Cases];

        types[0] = LockEntry.KeySkill;
        indices[0] = LockType.Picklock;
        skills[0] = 50;

        return DbcFixture.Store(e => e.Id, new LockEntry(SkillLock, types, indices, skills));
    }
}
