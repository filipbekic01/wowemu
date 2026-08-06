using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Protocol;
using WowEmu.WorldServer;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>The race and class masks on a starting-spell row.</summary>
public sealed class PlayerCreateSpellTests
{
    /// <summary>
    /// A mask of zero means everyone.
    /// </summary>
    /// <remarks>
    /// Most of the table is zero-masked — the rows every character shares, like Dodge and Unarmed.
    /// Testing the bit without that shortcut leaves every character knowing nothing at all.
    /// </remarks>
    [Fact]
    public void AMaskOfZero_MeansEveryone()
    {
        PlayerCreateSpell shared = new(RaceMask: 0, ClassMask: 0, SpellId: 81);

        Assert.True(shared.AppliesTo(race: 1, characterClass: 1));
        Assert.True(shared.AppliesTo(race: 10, characterClass: 9));
    }

    /// <summary>
    /// The masks are bits over one-based ids.
    /// </summary>
    /// <remarks>
    /// A warrior is class 1 and bit 0. Shifting by the class rather than by class minus one gives
    /// every character the wrong class's spells — quietly, because it still produces a plausible
    /// list.
    /// </remarks>
    [Fact]
    public void TheMasks_AreBitsOverOneBasedIds()
    {
        // Classmask 40 is bits 3 and 5, which is class 4 (rogue) and class 6 (death knight).
        PlayerCreateSpell dualWield = new(0, ClassMask: 40, SpellId: SpellBook.DualWieldSpell);

        Assert.True(dualWield.AppliesTo(race: 1, characterClass: 4));
        Assert.True(dualWield.AppliesTo(race: 1, characterClass: 6));

        Assert.False(dualWield.AppliesTo(race: 1, characterClass: 1));
        Assert.False(dualWield.AppliesTo(race: 1, characterClass: 5));
    }

    /// <summary>Both masks have to admit the character, not either one.</summary>
    [Fact]
    public void BothMasks_MustAdmitTheCharacter()
    {
        // Race 1 (human), class 1 (warrior).
        PlayerCreateSpell humanWarrior = new(RaceMask: 1, ClassMask: 1, SpellId: 78);

        Assert.True(humanWarrior.AppliesTo(1, 1));
        Assert.False(humanWarrior.AppliesTo(2, 1));
        Assert.False(humanWarrior.AppliesTo(1, 2));
    }
}

/// <summary>The spellbook itself.</summary>
public sealed class SpellBookTests
{
    /// <summary>Learning is idempotent, and says so.</summary>
    /// <remarks>
    /// The return value is what stops a re-learn sending a second "you have learned" to the client.
    /// </remarks>
    [Fact]
    public void Learning_IsIdempotent()
    {
        Player player = InventoryFixture.Player();

        Assert.True(player.Spells.Learn(133));
        Assert.False(player.Spells.Learn(133));

        Assert.Equal(1, player.Spells.Count);
        Assert.True(player.Spells.Knows(133));
    }

    /// <summary>
    /// Dual wield is a known spell, not a class trait.
    /// </summary>
    /// <remarks>
    /// Making it a trait gives every level-1 warrior an off-hand. It is spell 674: rogues and death
    /// knights start with it, and a warrior learns it from a trainer at level 20.
    /// </remarks>
    [Fact]
    public void DualWield_FollowsFromKnowingTheSpell()
    {
        Player player = InventoryFixture.Player();

        Assert.False(player.CanDualWield);

        player.Spells.Learn(SpellBook.DualWieldSpell);

        Assert.True(player.CanDualWield);

        player.Spells.Forget(SpellBook.DualWieldSpell);

        Assert.False(player.CanDualWield);
    }

    /// <summary>A restored book brings dual wield back with it.</summary>
    /// <remarks>
    /// Recomputed from the book rather than toggled, so a login cannot leave a rogue unable to hold
    /// two weapons because the restore path forgot to set a flag.
    /// </remarks>
    [Fact]
    public void RestoringABook_BringsDualWieldBack()
    {
        Player player = InventoryFixture.Player();

        player.Spells.Restore([133, SpellBook.DualWieldSpell, 2050]);

        Assert.Equal(3, player.Spells.Count);
        Assert.True(player.CanDualWield);
    }

    /// <summary>A book without it leaves dual wield off.</summary>
    [Fact]
    public void RestoringABookWithout_LeavesDualWieldOff()
    {
        Player player = InventoryFixture.Player();

        player.Spells.Restore([133, 2050]);

        Assert.False(player.CanDualWield);
    }

    /// <summary>
    /// A rogue can put a second weapon in its off hand; a warrior cannot.
    /// </summary>
    /// <remarks>
    /// The whole point of the spellbook as far as M6 is concerned. Before it existed the off-hand
    /// slot was refused for everybody.
    /// </remarks>
    [Fact]
    public void ARogue_CanEquipAnOffHandWeapon()
    {
        Player rogue = InventoryFixture.Player(characterClass: 4);
        Player warrior = InventoryFixture.Player(characterClass: 1);

        rogue.Spells.Learn(SpellBook.DualWieldSpell);

        ItemTemplate dagger = ItemFixture.Build(
            entry: 2504, itemClass: ItemClass.Weapon, inventoryType: InventoryType.Weapon);

        // Both are already holding one, so the off hand is the only candidate left.
        InventoryFixture.Place(
            rogue, dagger, new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));
        InventoryFixture.Place(
            warrior, dagger, new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        Assert.Equal(InventorySlots.OffHand, rogue.Inventory.FindEquipSlot(dagger));
        Assert.Equal(InventorySlots.None, warrior.Inventory.FindEquipSlot(dagger));
    }
}

/// <summary>The spellbook and trainer packets.</summary>
public sealed class SpellBookPacketTests
{
    private static readonly ObjectGuid Trainer = ObjectGuid.Create(HighGuid.Unit, 197, 5);

    /// <summary>
    /// The initial-spells count is sixteen bits, not thirty-two.
    /// </summary>
    /// <remarks>
    /// A spellbook of more than 65,535 is not a thing, but writing a word here shifts every spell
    /// that follows and the client reads the book as noise.
    /// </remarks>
    [Fact]
    public void TheInitialSpells_ReadBackFieldByField()
    {
        PacketWriter writer = new();
        InitialSpells.Write(writer, [81, 203, 674]);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt8(out byte leading));
        Assert.Equal(0, leading);

        Assert.True(reader.TryReadUInt16(out ushort count));
        Assert.Equal(3, count);

        foreach (uint expected in (uint[])[81, 203, 674])
        {
            Assert.True(reader.TryReadUInt32(out uint spellId));
            Assert.Equal(expected, spellId);

            Assert.True(reader.TryReadUInt16(out ushort notASlot));
            Assert.Equal(0, notASlot);
        }

        // The cooldown count, which is not optional even when it is zero.
        Assert.True(reader.TryReadUInt16(out ushort cooldowns));
        Assert.Equal(0, cooldowns);

        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>An empty book is still a well-formed packet.</summary>
    [Fact]
    public void AnEmptyBook_IsStillWellFormed()
    {
        PacketWriter writer = new();
        InitialSpells.Write(writer, []);

        Assert.Equal(1 + 2 + 2, writer.WrittenSpan.Length);
    }

    /// <summary>
    /// A trainer line carries two fixed arrays the client reads unconditionally.
    /// </summary>
    /// <remarks>
    /// Two words of point cost and three of required abilities. Both are zero here and both still
    /// have to be written, or the line after is read twenty bytes early.
    /// </remarks>
    [Fact]
    public void ATrainerLine_IsAFixedWidth()
    {
        PacketWriter writer = new();

        GossipPackets.WriteTrainerList(writer, Trainer, 2, string.Empty,
            [new TrainerLine(674, TrainerSpellState.Green, 1000, 20, 0, 0)]);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt64(out ulong trainer));
        Assert.Equal(Trainer.Value, trainer);

        Assert.True(reader.TryReadUInt32(out uint trainerType));
        Assert.Equal(2u, trainerType);

        Assert.True(reader.TryReadUInt32(out uint count));
        Assert.Equal(1u, count);

        Assert.True(reader.TryReadUInt32(out uint spellId));
        Assert.Equal(674u, spellId);

        Assert.True(reader.TryReadUInt8(out byte usable));
        Assert.Equal(TrainerSpellState.Green, usable);

        Assert.True(reader.TryReadUInt32(out uint cost));
        Assert.Equal(1000u, cost);

        // Two words of point cost.
        reader.Skip(4 + 4);

        Assert.True(reader.TryReadUInt8(out byte level));
        Assert.Equal(20, level);

        // Skill and rank, then three words of prerequisite spells.
        reader.Skip(4 + 4 + (3 * 4));

        Assert.True(reader.TryReadCString(out string? greeting));
        Assert.Equal(string.Empty, greeting);

        Assert.Equal(0, reader.Remaining);
    }
}

/// <summary>The starting-spell and trainer tables, over the real vendored rows.</summary>
public sealed class SpellStoreDataTests(ITestOutputHelper output)
{
    private static CancellationToken TestToken => CancellationToken.None;

    [RequiresWorldDatabaseFact]
    public async Task TheStores_LoadEveryRow()
    {
        PlayerSpellStore starting = new();
        TrainerStore trainers = new();

        await starting.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await trainers.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(starting.Count > 150, $"only {starting.Count} starting spell rows");
        Assert.True(trainers.RowCount > 4_000, $"only {trainers.RowCount} trainer rows");

        output.WriteLine($"{starting}; {trainers}");
    }

    /// <summary>
    /// A rogue starts knowing Dual Wield and a warrior does not.
    /// </summary>
    /// <remarks>
    /// The one starting spell with a visible mechanical consequence, and the reason the off-hand
    /// slot was refused for everybody before the spellbook existed.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task ARogue_StartsKnowingDualWield()
    {
        PlayerSpellStore starting = new();
        await starting.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        uint[] rogue = [.. starting.For(race: 1, characterClass: 4)];
        uint[] warrior = [.. starting.For(race: 1, characterClass: 1)];

        Assert.Contains(SpellBook.DualWieldSpell, rogue);
        Assert.DoesNotContain(SpellBook.DualWieldSpell, warrior);

        output.WriteLine($"human rogue knows {rogue.Length}, human warrior {warrior.Length}");
    }

    /// <summary>Every playable race and class starts with something.</summary>
    /// <remarks>
    /// A combination with nothing would be a character that cannot attack — the shared rows include
    /// Attack itself.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task EveryRaceAndClass_StartsWithSomething()
    {
        PlayerSpellStore starting = new();
        PlayerCreateInfoStore createInfo = new();

        await starting.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await createInfo.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        List<string> empty = [];
        int checkedPairs = 0;

        for (byte race = 1; race <= 11; race++)
        {
            for (byte characterClass = 1; characterClass <= 11; characterClass++)
            {
                if (!createInfo.TryGet(race, characterClass, out _))
                {
                    continue;
                }

                checkedPairs++;

                if (!starting.For(race, characterClass).Any())
                {
                    empty.Add($"race {race} class {characterClass}");
                }
            }
        }

        Assert.True(checkedPairs > 50, $"only {checkedPairs} playable combinations");
        Assert.Empty(empty);

        output.WriteLine($"{checkedPairs} playable combinations, all with starting spells");
    }

    /// <summary>
    /// A negative SpellID in npc_trainer is a reference, and is flattened away.
    /// </summary>
    /// <remarks>
    /// The fifth table to overload a column's sign this way, after the two loot tables,
    /// <c>npc_vendor</c> and the two quest columns.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task ANegativeTrainerSpell_IsAReferenceAndIsFlattenedAway()
    {
        TrainerStore trainers = new();
        await trainers.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        int expanded = 0;

        for (uint entry = 1; entry < 100_000; entry++)
        {
            foreach (TrainerSpell spell in trainers.For(entry))
            {
                Assert.True(spell.SpellId > 0, $"trainer {entry} still has a reference row");
                expanded++;
            }
        }

        Assert.True(expanded > 0, "no trainer rows at all");

        output.WriteLine($"{expanded} flattened rows from {trainers.RowCount} table rows");
    }

    /// <summary>Every spell a trainer teaches is one the client knows about.</summary>
    /// <remarks>
    /// A trainer line for a spell absent from <c>Spell.dbc</c> is a row the client cannot draw.
    /// </remarks>
    [RequiresClientDataFact]
    public async Task EveryTrainedSpell_IsInTheClientData()
    {
        TrainerStore trainers = new();
        await trainers.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        if (trainers.Count == 0)
        {
            return;
        }

        WowEmu.Data.Client.SpellStores spells =
            WowEmu.Data.Client.SpellStores.Load(ClientData.DbcDirectory);

        HashSet<uint> missing = [];
        int walked = 0;

        for (uint entry = 1; entry < 100_000; entry++)
        {
            foreach (TrainerSpell spell in trainers.For(entry))
            {
                walked++;

                if (!spells.Spells.TryGet(spell.SpellId, out _))
                {
                    missing.Add(spell.SpellId);
                }
            }
        }

        output.WriteLine($"{walked} trainer spells, {missing.Count} absent from Spell.dbc");

        Assert.True(walked > 0, "no trainer spells walked");
        Assert.Empty(missing);
    }
}
