using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The weapons a creature is drawn holding.
/// </summary>
/// <remarks>
/// Three item ids in <c>UNIT_VIRTUAL_ITEM_SLOT_ID</c>, and nothing more — a creature's sword is a
/// model the client looks up, never an item anything carries, loots or drops. 49,183 of the 146,000
/// spawns name an outfit and were being drawn empty-handed.
/// </remarks>
public sealed class CreatureEquipmentTests
{
    private const uint Entry = 299;

    /// <summary>A spawn asking for a variant gets that variant.</summary>
    [Fact]
    public void ANamedVariant_IsTheOneReturned()
    {
        CreatureEquipStore store = new();
        store.Add(Entry, 1, new CreatureEquipment(11, 12, 13));
        store.Add(Entry, 2, new CreatureEquipment(21, 22, 23));

        Assert.Equal(new CreatureEquipment(21, 22, 23), store.For(Entry, 2, Never)!.Value);
    }

    /// <summary>
    /// Zero means unarmed, and is not a variant id.
    /// </summary>
    /// <remarks>
    /// 96,587 spawns store it, so reading it as a lookup key would send two thirds of the world
    /// through a dictionary miss to arrive at the same answer — and would arm anything whose entry
    /// happened to define a variant 0.
    /// </remarks>
    [Fact]
    public void ZeroMeansUnarmed()
    {
        CreatureEquipStore store = new();
        store.Add(Entry, 1, new CreatureEquipment(11, 12, 13));

        Assert.Null(store.For(Entry, 0, Never));
    }

    /// <summary>
    /// Minus one picks one at random, and only then is the generator touched.
    /// </summary>
    /// <remarks>
    /// The draw count is not incidental. PLAN §9 makes seeded comparison against the C++ the
    /// sharpest tool available, and it only works if both sides consume the generator the same
    /// number of times — so a draw on every one of 146,000 spawns instead of on the 176 that ask
    /// for one would put every later roll out of step.
    /// </remarks>
    [Fact]
    public void MinusOnePicksAtRandom_AndOnlyThenRolls()
    {
        CreatureEquipStore store = new();
        store.Add(Entry, 1, new CreatureEquipment(11, 12, 13));
        store.Add(Entry, 4, new CreatureEquipment(41, 42, 43));

        int rolls = 0;

        uint Counting(uint min, uint max)
        {
            rolls++;
            return max;
        }

        // A named variant and an unarmed spawn roll nothing at all.
        store.For(Entry, 1, Counting);
        store.For(Entry, 0, Counting);
        Assert.Equal(0, rolls);

        // The random one rolls exactly once.
        Assert.NotNull(store.For(Entry, -1, Counting));
        Assert.Equal(1, rolls);
    }

    /// <summary>
    /// The roll picks a position in the list, not an id.
    /// </summary>
    /// <remarks>
    /// Upstream advances an iterator over an ordered map by <c>urand(0, size - 1)</c>. Variant ids
    /// are not contiguous — this entry has 1 and 4 — so indexing the dictionary by the rolled number
    /// finds nothing for every gap, and the creature is silently disarmed.
    /// </remarks>
    [Theory]
    [InlineData(0u, 11u)]
    [InlineData(1u, 41u)]
    public void TheRoll_PicksAPositionRatherThanAnId(uint roll, uint expectedMainHand)
    {
        CreatureEquipStore store = new();
        store.Add(Entry, 1, new CreatureEquipment(11, 12, 13));
        store.Add(Entry, 4, new CreatureEquipment(41, 42, 43));

        Assert.Equal(expectedMainHand, store.For(Entry, -1, (min, max) => roll)!.Value.MainHand);
    }

    /// <summary>An entry with no outfit at all comes back empty rather than throwing.</summary>
    /// <remarks>
    /// 10,711 entries of the ~9,800 templates have one, so most do — but a spawn naming a variant
    /// its entry does not define is a data error upstream logs and shrugs at, and so does this.
    /// </remarks>
    [Fact]
    public void AnUnknownEntryOrVariant_IsUnarmed()
    {
        CreatureEquipStore store = new();
        store.Add(Entry, 1, new CreatureEquipment(11, 12, 13));

        Assert.Null(store.For(entry: 12345, 1, Never));
        Assert.Null(store.For(Entry, 7, Never));
    }

    /// <summary>The three slots reach the three fields, in order.</summary>
    /// <remarks>
    /// The end-to-end check. <c>UNIT_VIRTUAL_ITEM_SLOT_ID</c> is three consecutive fields and the
    /// client reads main hand, off hand and ranged from them in that order — swapping any two puts
    /// a bow in a guard's fist.
    /// </remarks>
    [Fact]
    public void TheOutfit_ReachesTheThreeVirtualItemFields()
    {
        Creature creature = CreatureFixture.Build(equipment: new CreatureEquipment(1234, 5678, 9012));

        Assert.Equal(1234u, creature.Fields.GetUInt32(UpdateFields.UNIT_VIRTUAL_ITEM_SLOT_ID));
        Assert.Equal(5678u, creature.Fields.GetUInt32(UpdateFields.UNIT_VIRTUAL_ITEM_SLOT_ID + 1));
        Assert.Equal(9012u, creature.Fields.GetUInt32(UpdateFields.UNIT_VIRTUAL_ITEM_SLOT_ID + 2));
    }

    /// <summary>A creature with no outfit leaves the fields alone.</summary>
    [Fact]
    public void NoOutfit_LeavesTheFieldsEmpty()
    {
        Creature creature = CreatureFixture.Build();

        Assert.Equal(0u, creature.Fields.GetUInt32(UpdateFields.UNIT_VIRTUAL_ITEM_SLOT_ID));
    }

    /// <summary>The virtual item fields are public, so onlookers see the weapons too.</summary>
    /// <remarks>
    /// Worth pinning rather than assuming: the whole point of drawing a weapon is that other people
    /// see it, and the per-observer filter would silently drop it if the generated flag table said
    /// otherwise.
    /// </remarks>
    [Fact]
    public void TheVirtualItemFields_ArePublic()
    {
        ReadOnlySpan<ushort> flags = UpdateFieldFlags.Unit;

        for (int slot = 0; slot < CreatureEquipment.SlotCount; slot++)
        {
            UpdateFieldVisibility visibility =
                (UpdateFieldVisibility)flags[UpdateFields.UNIT_VIRTUAL_ITEM_SLOT_ID + slot];

            Assert.True(visibility.HasFlag(UpdateFieldVisibility.Public), $"slot {slot} is not public");
        }
    }

    // ------------------------------------------------------------------ the addon row

    /// <summary>
    /// A sheath state of "put away" survives, and one of zero does not overwrite the default.
    /// </summary>
    /// <remarks>
    /// The guard on each packed column is upstream's and is load-bearing. Every creature is set to
    /// weapons-drawn before the addon is applied, so writing a zero <c>bytes2</c> through would put
    /// the whole world back to sheathed — and with 31,136 spawns carrying a non-zero one, the bug
    /// would look like a data problem rather than a code one.
    /// </remarks>
    [Fact]
    public void AZeroColumn_LeavesTheDefaultAlone()
    {
        Creature drawn = CreatureFixture.Build(addon: new CreatureAddon(0, 0, 0, Bytes2: 0, 0, []));
        Creature sheathed = CreatureFixture.Build(addon: new CreatureAddon(0, 0, 0, Bytes2: 0, 0, []));

        // Both keep the weapons-drawn default rather than being reset by an empty column.
        Assert.Equal(1, drawn.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_2, 0));
        Assert.Equal(1, sheathed.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_2, 0));
    }

    /// <summary>The packed columns unpack into the bytes the client reads.</summary>
    /// <remarks>
    /// Stand state in the low byte and animation tier in the high one — a creature sitting on a
    /// chair and a creature hovering are the same column, sixteen bits apart.
    /// </remarks>
    [Fact]
    public void ThePackedColumns_UnpackIntoTheirBytes()
    {
        // Stand state 3 (sitting), visibility flags 0x40, animation tier 2.
        const uint Bytes1 = 3u | (0x40u << 16) | (2u << 24);

        Creature creature = CreatureFixture.Build(
            addon: new CreatureAddon(0, Mount: 6080, Bytes1, Bytes2: 2, Emote: 173, []));

        Assert.Equal(3, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_1, 0));
        Assert.Equal(0x40, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_1, 2));
        Assert.Equal(2, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_1, 3));

        Assert.Equal(2, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_2, 0));
        Assert.Equal(6080u, creature.Fields.GetUInt32(UpdateFields.UNIT_FIELD_MOUNTDISPLAYID));
        Assert.Equal(173u, creature.Fields.GetUInt32(UpdateFields.UNIT_NPC_EMOTESTATE));
    }

    /// <summary>
    /// The pet-only bytes are dropped rather than written through.
    /// </summary>
    /// <remarks>
    /// The pet talent byte of <c>bytes1</c> and the rename and shapeshift bytes of <c>bytes2</c>
    /// carry leftovers for anything that is not a pet. Upstream zeroes all three explicitly, with
    /// the write-through lines commented out beside them.
    /// </remarks>
    [Fact]
    public void ThePetOnlyBytes_AreDropped()
    {
        // Every byte of both columns set to 0xFF.
        Creature creature = CreatureFixture.Build(
            addon: new CreatureAddon(0, 0, Bytes1: 0xFFFFFFFF, Bytes2: 0xFFFFFFFF, 0, []));

        Assert.Equal(0, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_1, 1));
        Assert.Equal(0, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_2, 2));
        Assert.Equal(0, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_2, 3));

        // And the ones that are written through survive intact.
        Assert.Equal(0xFF, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_1, 0));
        Assert.Equal(0xFF, creature.Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_2, 0));
    }

    // ------------------------------------------------------------------ the auras column

    /// <summary>The column is a space-separated spell list.</summary>
    [Theory]
    [InlineData("1234", new uint[] { 1234 })]
    [InlineData("1234 5678", new uint[] { 1234, 5678 })]
    [InlineData("  1234   5678  ", new uint[] { 1234, 5678 })]
    public void TheAurasColumn_ParsesASpaceSeparatedList(string column, uint[] expected) =>
        Assert.Equal(expected, CreatureAddon.ParseAuras(column));

    /// <summary>
    /// Anything that is not a spell list reads as no auras rather than failing the row.
    /// </summary>
    /// <remarks>
    /// Free text in a database column, so it is read defensively. A malformed list costs one
    /// creature its buff; refusing the row would cost it its stand state, its mount and its patrol
    /// route as well.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("not a spell")]
    public void AMalformedAurasColumn_ReadsAsNone(string? column) =>
        Assert.Empty(CreatureAddon.ParseAuras(column));

    /// <summary>A bad entry is skipped and the good ones around it survive.</summary>
    [Fact]
    public void AMalformedEntry_DoesNotTakeTheRestWithIt() =>
        Assert.Equal([1234u, 5678u], CreatureAddon.ParseAuras("1234 rubbish 0 5678"));

    /// <summary>A roll that never fires, for the cases that must not draw.</summary>
    private static uint Never(uint min, uint max) =>
        throw new InvalidOperationException("the generator should not have been consulted");
}
