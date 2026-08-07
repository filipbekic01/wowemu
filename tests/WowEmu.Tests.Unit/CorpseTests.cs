using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The body a player leaves behind.
/// </summary>
/// <remarks>
/// It was a remembered position on the player until now — enough to walk back to and reclaim, and
/// invisible to everyone including its owner. The client draws the body from a real object, and it
/// offers the resurrect dialog because one is nearby.
/// </remarks>
public sealed class CorpseTests
{
    /// <summary>A corpse belongs to whoever died, and stands where they fell.</summary>
    [Fact]
    public void ACorpse_BelongsToTheDeadAndStandsWhereTheyFell()
    {
        Player player = InventoryFixture.Player();
        player.Position = new Position(10f, 20f, 30f, 1f);

        Corpse corpse = Corpse.Create(player, 1);

        Assert.Equal(player.Guid, corpse.Owner);
        Assert.Equal(10f, corpse.Position.X);
        Assert.Equal(30f, corpse.Position.Z);
        Assert.True(corpse.IsResurrectable);
    }

    /// <summary>
    /// The appearance is repacked, not copied across.
    /// </summary>
    /// <remarks>
    /// A player keeps skin, face, hair style and hair colour in one word and facial hair in another;
    /// a corpse wants race and gender in the first word alongside skin, and everything else in the
    /// second. Copying the player's words straight over gives a body with somebody else's face, and
    /// it looks like a plausible character rather than like a bug.
    /// </remarks>
    [Fact]
    public void TheAppearance_IsRepackedNotCopied()
    {
        Player player = InventoryFixture.Player(race: 1);

        // skin 0x11, face 0x22, hair style 0x33, hair colour 0x44, facial hair 0x55.
        player.Fields.SetUInt32(UpdateFields.PLAYER_BYTES, 0x44332211);
        player.Fields.SetUInt32(UpdateFields.PLAYER_BYTES_2, 0x00000055);
        player.Fields.SetUInt32(UpdateFields.PLAYER_BYTES_3, 0x00000001);

        Corpse corpse = Corpse.Create(player, 1);

        uint bytes1 = corpse.Fields.GetUInt32(UpdateFields.CORPSE_FIELD_BYTES_1);
        uint bytes2 = corpse.Fields.GetUInt32(UpdateFields.CORPSE_FIELD_BYTES_2);

        // Low byte is deliberately zero; then race, gender, skin.
        Assert.Equal(0x00u, bytes1 & 0xFF);
        Assert.Equal(1u, (bytes1 >> 8) & 0xFF);
        Assert.Equal(1u, (bytes1 >> 16) & 0xFF);
        Assert.Equal(0x11u, (bytes1 >> 24) & 0xFF);

        // Face, hair style, hair colour, facial hair.
        Assert.Equal(0x22u, bytes2 & 0xFF);
        Assert.Equal(0x33u, (bytes2 >> 8) & 0xFF);
        Assert.Equal(0x44u, (bytes2 >> 16) & 0xFF);
        Assert.Equal(0x55u, (bytes2 >> 24) & 0xFF);

        // And it is genuinely a different arrangement, not the same word twice.
        Assert.NotEqual(player.Fields.GetUInt32(UpdateFields.PLAYER_BYTES), bytes1);
    }

    /// <summary>
    /// Equipment is a display id and an inventory type, not an item.
    /// </summary>
    /// <remarks>
    /// The client renders the body from these directly. Writing an item guid or an entry — either of
    /// which is the obvious thing to reach for — draws whatever model happens to share that number.
    /// </remarks>
    [Fact]
    public void Equipment_IsADisplayIdAndAnInventoryType()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate chest = ItemFixture.Build(entry: 5000, inventoryType: InventoryType.Chest)
            with { DisplayId = 1234 };

        InventoryFixture.Place(
            player, chest, new ItemPosition(InventorySlots.Backpack, InventorySlots.Chest));

        Corpse corpse = Corpse.Create(player, 1);

        uint packed = corpse.Fields.GetUInt32(UpdateFields.CORPSE_FIELD_ITEM + InventorySlots.Chest);

        Assert.Equal(1234u, packed & 0xFFFFFF);
        Assert.Equal(InventoryType.Chest, (byte)(packed >> 24));
    }

    /// <summary>An empty slot stays empty.</summary>
    [Fact]
    public void AnEmptySlot_StaysEmpty()
    {
        Corpse corpse = Corpse.Create(InventoryFixture.Player(), 1);

        Assert.Equal(0u, corpse.Fields.GetUInt32(UpdateFields.CORPSE_FIELD_ITEM + InventorySlots.Head));
    }

    /// <summary>
    /// Bones cannot be resurrected at, and the object survives the change.
    /// </summary>
    /// <remarks>
    /// Converted rather than replaced, so every client already told about the corpse sees it change
    /// instead of vanishing and something new appearing in the same spot.
    /// </remarks>
    [Fact]
    public void Bones_CannotBeResurrectedAt()
    {
        Player player = InventoryFixture.Player();
        Corpse corpse = Corpse.Create(player, 1);

        ObjectGuid before = corpse.Guid;

        corpse.ConvertToBones();

        Assert.False(corpse.IsResurrectable);
        Assert.Equal(before, corpse.Guid);
        Assert.Equal(ObjectGuid.Empty, corpse.Owner);

        uint flags = corpse.Fields.GetUInt32(UpdateFields.CORPSE_FIELD_FLAGS);
        Assert.NotEqual(0u, flags & CorpseFlags.Bones);
    }
}

/// <summary>
/// A corpse in the world, seen by the people around it.
/// </summary>
public sealed class MapCorpseTests
{
    /// <summary>
    /// The body appears when the player releases, not when they die.
    /// </summary>
    /// <remarks>
    /// Until a player releases, the body standing there <i>is</i> their own character, still
    /// rendered dead. Creating the corpse at death would put two of them in the world at once.
    /// </remarks>
    [Fact]
    public void TheBody_AppearsOnRelease()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        map.KillPlayer(player);

        Assert.Null(map.CorpseOf(player.Guid));

        map.ReleaseSpirit(player);

        Assert.NotNull(map.CorpseOf(player.Guid));
    }

    /// <summary>And it goes away when they take it back.</summary>
    /// <remarks>
    /// A living player standing beside their own resurrectable corpse is offered the dialog again,
    /// which resurrects them a second time from nothing.
    /// </remarks>
    [Fact]
    public void TheBody_GoesAwayOnReclaim()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        map.KillPlayer(player);
        map.ReleaseSpirit(player);
        map.Relocate(player, player.CorpsePosition);

        player.GhostTime -= player.ReclaimDelaySeconds + 1;

        Assert.True(map.ReclaimCorpse(player));
        Assert.Null(map.CorpseOf(player.Guid));
    }

    /// <summary>The spirit healer takes it too.</summary>
    [Fact]
    public void TheSpiritHealer_TakesTheBodyToo()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        map.KillPlayer(player);
        map.ReleaseSpirit(player);

        Assert.NotNull(map.CorpseOf(player.Guid));

        map.SpiritHealerResurrect(player);

        Assert.Null(map.CorpseOf(player.Guid));
    }

    /// <summary>
    /// Dying twice before reclaiming leaves one body, not two.
    /// </summary>
    /// <remarks>
    /// Two resurrectable corpses means a player could pick either, and one of them would outlive
    /// them — a body standing in the world with an owner who is alive.
    /// </remarks>
    [Fact]
    public void DyingTwice_LeavesOneBody()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        map.KillPlayer(player);
        map.ReleaseSpirit(player);

        Corpse first = Assert.IsType<Corpse>(map.CorpseOf(player.Guid));

        player.DeathState = DeathState.Alive;
        player.IsGhost = false;
        player.Health = 1;

        map.KillPlayer(player);
        map.ReleaseSpirit(player);

        Corpse second = Assert.IsType<Corpse>(map.CorpseOf(player.Guid));

        Assert.NotEqual(first.Guid, second.Guid);
        Assert.False(first.IsResurrectable);
    }

    /// <summary>
    /// Other people can see it, which is the whole point of it being an object.
    /// </summary>
    /// <remarks>
    /// A corpse that only its owner knows about is the remembered position this replaced.
    /// </remarks>
    [Fact]
    public void OtherPeople_CanSeeIt()
    {
        (Map map, Player player, _, _) = MapCombatFixture.Engaged();

        Player onlooker = InventoryFixture.Player();
        onlooker.Position = player.Position;
        map.Add(onlooker);

        map.KillPlayer(player);
        map.ReleaseSpirit(player);

        Corpse corpse = Assert.IsType<Corpse>(map.CorpseOf(player.Guid));

        Assert.Contains(corpse.Guid, onlooker.VisibleObjects);
    }
}
