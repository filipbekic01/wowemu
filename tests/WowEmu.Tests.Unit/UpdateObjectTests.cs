using System.IO.Compression;
using WowEmu.Core;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>Update-field storage: typed accessors, dirty tracking, and the two awkward field types.</summary>
public sealed class UpdateFieldStorageTests
{
    [Fact]
    public void NewStorage_IsAllZeroAndClean()
    {
        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);

        Assert.Equal(1326, fields.FieldCount);
        Assert.False(fields.IsDirty);
        Assert.Equal(0u, fields.GetUInt32(UpdateFields.UNIT_FIELD_HEALTH));
    }

    [Fact]
    public void SettingAValue_MarksItDirty()
    {
        UpdateFieldStorage fields = new(UpdateFields.UNIT_END);

        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 42);

        Assert.True(fields.IsDirty);
        Assert.True(fields.IsFieldDirty(UpdateFields.UNIT_FIELD_HEALTH));
        Assert.Equal(42u, fields.GetUInt32(UpdateFields.UNIT_FIELD_HEALTH));
    }

    /// <summary>
    /// Rewriting the same value must not dirty the field. A field marked dirty is re-sent to every
    /// observer, so a system that rewrites a constant each tick would broadcast continuously.
    /// </summary>
    [Fact]
    public void RewritingTheSameValue_DoesNotDirtyIt()
    {
        UpdateFieldStorage fields = new(UpdateFields.UNIT_END);

        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 42);
        fields.ClearDirty();

        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 42);

        Assert.False(fields.IsDirty);
        Assert.False(fields.IsFieldDirty(UpdateFields.UNIT_FIELD_HEALTH));
    }

    [Fact]
    public void ClearDirty_KeepsTheValues()
    {
        UpdateFieldStorage fields = new(UpdateFields.UNIT_END);

        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 100);
        fields.ClearDirty();

        Assert.False(fields.IsDirty);
        Assert.Equal(100u, fields.GetUInt32(UpdateFields.UNIT_FIELD_HEALTH));
    }

    [Fact]
    public void Floats_RoundTripThroughTheirBits()
    {
        UpdateFieldStorage fields = new(UpdateFields.OBJECT_END);

        fields.SetFloat(UpdateFields.OBJECT_FIELD_SCALE_X, 1.25f);

        Assert.Equal(1.25f, fields.GetFloat(UpdateFields.OBJECT_FIELD_SCALE_X));
    }

    /// <summary>A guid spans two slots. Writing only one leaves the client tracking a different object.</summary>
    [Fact]
    public void Guids_OccupyTwoConsecutiveFields()
    {
        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);

        // A creature guid, so both halves are non-zero: the high word carries 0xF130.
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Unit, entry: 299, counter: 0x123456);

        fields.SetGuid(UpdateFields.OBJECT_FIELD_GUID, guid);

        Assert.Equal(guid, fields.GetGuid(UpdateFields.OBJECT_FIELD_GUID));
        Assert.Equal((uint)(guid.Value & 0xFFFFFFFF), fields.GetUInt32(UpdateFields.OBJECT_FIELD_GUID));
        Assert.Equal((uint)(guid.Value >> 32), fields.GetUInt32(UpdateFields.OBJECT_FIELD_GUID + 1));

        Assert.True(fields.IsFieldDirty(UpdateFields.OBJECT_FIELD_GUID));
        Assert.True(fields.IsFieldDirty(UpdateFields.OBJECT_FIELD_GUID + 1));
    }

    /// <summary>
    /// A player guid's high word is zero, so assigning one into zeroed storage dirties only the low
    /// half. That is correct — the client already has zero there — but it is surprising enough to
    /// pin down.
    /// </summary>
    [Fact]
    public void PlayerGuid_DirtiesOnlyTheHalfThatChanged()
    {
        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Player, 0x12345678);

        fields.SetGuid(UpdateFields.OBJECT_FIELD_GUID, guid);

        Assert.Equal(guid, fields.GetGuid(UpdateFields.OBJECT_FIELD_GUID));
        Assert.True(fields.IsFieldDirty(UpdateFields.OBJECT_FIELD_GUID));
        Assert.False(fields.IsFieldDirty(UpdateFields.OBJECT_FIELD_GUID + 1));
    }

    /// <summary>
    /// A BYTES field is four independent values in one slot — skin, face, hair style, hair colour
    /// for a player. Writing one must leave the other three alone.
    /// </summary>
    [Fact]
    public void ByteFields_ArePackedIndependently()
    {
        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);

        fields.SetByte(UpdateFields.PLAYER_BYTES, 0, 1);   // skin
        fields.SetByte(UpdateFields.PLAYER_BYTES, 1, 2);   // face
        fields.SetByte(UpdateFields.PLAYER_BYTES, 2, 3);   // hair style
        fields.SetByte(UpdateFields.PLAYER_BYTES, 3, 4);   // hair colour

        Assert.Equal(1, fields.GetByte(UpdateFields.PLAYER_BYTES, 0));
        Assert.Equal(2, fields.GetByte(UpdateFields.PLAYER_BYTES, 1));
        Assert.Equal(3, fields.GetByte(UpdateFields.PLAYER_BYTES, 2));
        Assert.Equal(4, fields.GetByte(UpdateFields.PLAYER_BYTES, 3));

        Assert.Equal(0x04030201u, fields.GetUInt32(UpdateFields.PLAYER_BYTES));
    }

    [Fact]
    public void ShortHalves_ArePackedIndependently()
    {
        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);

        fields.SetUInt16(UpdateFields.PLAYER_BYTES, 0, 0x1111);
        fields.SetUInt16(UpdateFields.PLAYER_BYTES, 1, 0x2222);

        Assert.Equal(0x1111, fields.GetUInt16(UpdateFields.PLAYER_BYTES, 0));
        Assert.Equal(0x2222, fields.GetUInt16(UpdateFields.PLAYER_BYTES, 1));
        Assert.Equal(0x22221111u, fields.GetUInt32(UpdateFields.PLAYER_BYTES));
    }

    [Fact]
    public void Flags_SetAndClearIndividualBits()
    {
        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);

        fields.SetFlag(UpdateFields.UNIT_FIELD_FLAGS, 0x0008);
        fields.SetFlag(UpdateFields.UNIT_FIELD_FLAGS, 0x0100);

        Assert.True(fields.HasFlag(UpdateFields.UNIT_FIELD_FLAGS, 0x0008));
        Assert.True(fields.HasFlag(UpdateFields.UNIT_FIELD_FLAGS, 0x0100));

        fields.RemoveFlag(UpdateFields.UNIT_FIELD_FLAGS, 0x0008);

        Assert.False(fields.HasFlag(UpdateFields.UNIT_FIELD_FLAGS, 0x0008));
        Assert.True(fields.HasFlag(UpdateFields.UNIT_FIELD_FLAGS, 0x0100));
    }

    [Fact]
    public void ByteOffsetsBeyondTheSlot_AreRejected()
    {
        UpdateFieldStorage fields = new(UpdateFields.OBJECT_END);

        Assert.Throws<ArgumentOutOfRangeException>(() => fields.SetByte(0, 4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fields.SetUInt16(0, 2, 1));
    }
}

/// <summary>
/// The update mask.
/// </summary>
/// <remarks>
/// The mask is the only thing telling the client which value is which — the values that follow
/// carry no indices. One bit wrong and every value after it lands in the wrong field.
/// </remarks>
public sealed class UpdateMaskTests
{
    [Fact]
    public void BlockCount_RoundsUpToWholeWords()
    {
        Assert.Equal(1, new UpdateMask(1).BlockCount);
        Assert.Equal(1, new UpdateMask(32).BlockCount);
        Assert.Equal(2, new UpdateMask(33).BlockCount);

        // A player's 1326 fields need 42 blocks — and the count is a byte, so it just fits.
        Assert.Equal(42, new UpdateMask(UpdateFields.PLAYER_END).BlockCount);
        Assert.True(new UpdateMask(UpdateFields.PLAYER_END).BlockCount <= byte.MaxValue);
    }

    [Fact]
    public void SetBits_LandInTheRightWordAndPosition()
    {
        UpdateMask mask = new(64);
        mask.Set(0);
        mask.Set(31);
        mask.Set(32);

        PacketWriter writer = new();
        mask.WriteTo(writer);

        byte[] bytes = writer.ToArray();
        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadUInt8(out byte blocks));
        Assert.Equal(2, blocks);

        Assert.True(reader.TryReadUInt32(out uint first));
        Assert.Equal(0x80000001u, first);          // bits 0 and 31

        Assert.True(reader.TryReadUInt32(out uint second));
        Assert.Equal(0x00000001u, second);         // bit 32 is bit 0 of word 1
    }

    [Fact]
    public void SetCount_MatchesTheNumberOfValuesThatFollow()
    {
        UpdateMask mask = new(100);
        mask.Set(5);
        mask.Set(50);
        mask.Set(99);

        Assert.Equal(3, mask.SetCount);
    }

    [Fact]
    public void IntersectWith_KeepsOnlySharedBits()
    {
        UpdateMask mask = new(64);
        mask.Set(1);
        mask.Set(2);

        UpdateMask visible = new(64);
        visible.Set(2);
        visible.Set(3);

        mask.IntersectWith(visible);

        Assert.False(mask.IsSet(1));
        Assert.True(mask.IsSet(2));
        Assert.False(mask.IsSet(3));
    }
}

/// <summary>The blocks and the packet that carries them.</summary>
public sealed class UpdateObjectPacketTests
{
    /// <summary>
    /// Walks a create block field by field. Nothing in it carries a length, so a section that is
    /// one byte wrong shifts everything after it — a disconnect rather than an error.
    /// </summary>
    [Fact]
    public void CreateBlock_HasEverySectionInOrder()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Player, 7);

        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);
        fields.SetGuid(UpdateFields.OBJECT_FIELD_GUID, guid);
        fields.SetUInt32(UpdateFields.OBJECT_FIELD_TYPE, 0x19);
        fields.SetFloat(UpdateFields.OBJECT_FIELD_SCALE_X, 1.0f);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 20);

        MovementInfo movement = new()
        {
            Position = new Position(-8949.95f, -132.493f, 83.5312f, 0f),
            Time = 12345,
        };

        byte[] block = UpdateBlockBuilder.BuildCreateBlock(
            guid, TypeId.Player, fields, movement, new MovementSpeeds(), isSelf: true);

        PacketReader reader = new(block);

        Assert.True(reader.TryReadUInt8(out byte updateType));
        Assert.Equal((byte)UpdateType.CreateObject2, updateType);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid readGuid));
        Assert.Equal(guid, readGuid);

        Assert.True(reader.TryReadUInt8(out byte typeId));
        Assert.Equal((byte)TypeId.Player, typeId);

        Assert.True(reader.TryReadUInt16(out ushort updateFlags));
        Assert.Equal(
            (ushort)(UpdateFlag.Living | UpdateFlag.StationaryPosition | UpdateFlag.Self),
            updateFlags);

        // Movement block.
        Assert.True(reader.TryReadUInt32(out uint movementFlags));
        Assert.Equal(0u, movementFlags);
        Assert.True(reader.TryReadUInt16(out _));                 // extra flags
        Assert.True(reader.TryReadUInt32(out uint time));
        Assert.Equal(12345u, time);

        Assert.Equal(-8949.95f, ReadFloat(ref reader), 0.001f);
        Assert.Equal(-132.493f, ReadFloat(ref reader), 0.001f);
        Assert.Equal(83.5312f, ReadFloat(ref reader), 0.001f);
        Assert.Equal(0f, ReadFloat(ref reader), 0.001f);          // orientation

        Assert.True(reader.TryReadUInt32(out uint fallTime));
        Assert.Equal(0u, fallTime);

        // Nine speeds, run second.
        Assert.Equal(2.5f, ReadFloat(ref reader), 0.001f);
        Assert.Equal(7.0f, ReadFloat(ref reader), 0.001f);
        for (int i = 0; i < 7; i++)
        {
            ReadFloat(ref reader);
        }

        // Values: block count, mask words, then the set values in ascending index order.
        Assert.True(reader.TryReadUInt8(out byte blockCount));
        Assert.Equal(42, blockCount);

        for (int i = 0; i < blockCount; i++)
        {
            Assert.True(reader.TryReadUInt32(out _));
        }

        // Four fields were set, but the guid's high half is zero and a create mask only carries
        // non-zero fields — so three values follow.
        Assert.True(reader.TryReadUInt32(out uint guidLow));
        Assert.Equal(7u, guidLow);
        Assert.True(reader.TryReadUInt32(out uint objectType));
        Assert.Equal(0x19u, objectType);
        Assert.True(reader.TryReadUInt32(out uint scaleBits));
        Assert.Equal(1.0f, BitConverter.UInt32BitsToSingle(scaleBits));
        Assert.True(reader.TryReadUInt32(out uint health));
        Assert.Equal(20u, health);

        Assert.True(reader.Ok);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// A create block sends every non-zero field, because the observer has no previous copy —
    /// unlike a values block, which sends only what changed.
    /// </summary>
    [Fact]
    public void CreateMask_CoversNonZeroFields_NotJustDirtyOnes()
    {
        UpdateFieldStorage fields = new(UpdateFields.UNIT_END);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 100);
        fields.ClearDirty();

        byte[] create = UpdateBlockBuilder.BuildCreateBlock(
            ObjectGuid.Create(HighGuid.Unit, 1, 1), TypeId.Unit, fields,
            new MovementInfo(), new MovementSpeeds(), isSelf: false);

        byte[] values = UpdateBlockBuilder.BuildValuesBlock(ObjectGuid.Create(HighGuid.Unit, 1, 1), fields);

        // The create block still carries health; the values block has nothing to say.
        Assert.True(create.Length > values.Length);
    }

    [Fact]
    public void ValuesBlock_CarriesOnlyChangedFields()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Player, 3);

        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 100);
        fields.ClearDirty();

        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 90);

        byte[] block = UpdateBlockBuilder.BuildValuesBlock(guid, fields);
        PacketReader reader = new(block);

        Assert.True(reader.TryReadUInt8(out byte updateType));
        Assert.Equal((byte)UpdateType.Values, updateType);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid readGuid));
        Assert.Equal(guid, readGuid);

        Assert.True(reader.TryReadUInt8(out byte blockCount));
        for (int i = 0; i < blockCount; i++)
        {
            Assert.True(reader.TryReadUInt32(out _));
        }

        Assert.True(reader.TryReadUInt32(out uint health));
        Assert.Equal(90u, health);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// A create block for someone else drops the fields they are not entitled to.
    /// </summary>
    /// <remarks>
    /// Upstream applies the same visibility test to a create block as to a values block —
    /// <c>BuildValuesUpdate</c> only swaps <c>_changesMask</c> for "is it non-zero". Before this,
    /// walking past a player broadcast their coinage to everyone in sight, because the create mask
    /// went out unfiltered.
    /// </remarks>
    [Fact]
    public void CreateBlock_ForAStranger_OmitsPrivateFields()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Player, 7);

        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 100);
        fields.SetUInt32(UpdateFields.PLAYER_FIELD_COINAGE, 12345);

        byte[] stranger = UpdateBlockBuilder.BuildCreateBlock(
            guid, TypeId.Player, fields, new MovementInfo(), new MovementSpeeds(), isSelf: false,
            UpdateFieldVisibilityRules.VisibleTo(UpdateObjectKind.Unit, isSelf: false, isOwner: false));

        byte[] self = UpdateBlockBuilder.BuildCreateBlock(
            guid, TypeId.Player, fields, new MovementInfo(), new MovementSpeeds(), isSelf: true);

        Assert.False(Contains(stranger, 12345u), "a stranger's create block carried the coinage");
        Assert.True(Contains(self, 12345u), "the player's own create block lost the coinage");

        // The mask is the same length either way — a cleared bit costs no bytes there — so the
        // stranger's block is shorter by exactly the value that was dropped.
        Assert.Equal(4, self.Length - stranger.Length);
    }

    /// <summary>
    /// A change nobody else may see produces no block at all, rather than an empty one.
    /// </summary>
    /// <remarks>
    /// An empty values block is not free: a player's mask is 42 words, so 170-odd bytes per observer
    /// per tick to say nothing. Picking up copper dirties only private fields, and that is the
    /// ordinary case rather than a corner one.
    /// </remarks>
    [Fact]
    public void ValuesBlock_ForAStranger_IsSkippedWhenNothingSurvivesTheFilter()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Player, 7);

        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);
        fields.SetUInt32(UpdateFields.PLAYER_FIELD_COINAGE, 500);

        Assert.False(UpdateBlockBuilder.TryBuildValuesBlock(
            guid,
            fields,
            UpdateObjectKind.Unit,
            UpdateFieldVisibilityRules.VisibleTo(UpdateObjectKind.Unit, isSelf: false, isOwner: false),
            out byte[]? block));

        Assert.Null(block);
    }

    /// <summary>And a public change does produce one, carrying only the public half.</summary>
    [Fact]
    public void ValuesBlock_ForAStranger_CarriesThePublicChangesOnly()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Player, 7);

        UpdateFieldStorage fields = new(UpdateFields.PLAYER_END);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 100);
        fields.SetUInt32(UpdateFields.PLAYER_FIELD_COINAGE, 500);
        fields.ClearDirty();

        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, 60);
        fields.SetUInt32(UpdateFields.PLAYER_FIELD_COINAGE, 499);

        Assert.True(UpdateBlockBuilder.TryBuildValuesBlock(
            guid,
            fields,
            UpdateObjectKind.Unit,
            UpdateFieldVisibilityRules.VisibleTo(UpdateObjectKind.Unit, isSelf: false, isOwner: false),
            out byte[]? block));

        PacketReader reader = new(block!);

        Assert.True(reader.TryReadUInt8(out byte updateType));
        Assert.Equal((byte)UpdateType.Values, updateType);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid readGuid));
        Assert.Equal(guid, readGuid);

        Assert.True(reader.TryReadUInt8(out byte blockCount));
        for (int i = 0; i < blockCount; i++)
        {
            Assert.True(reader.TryReadUInt32(out _));
        }

        // Exactly one value: the health. The coinage changed too and is not here.
        Assert.True(reader.TryReadUInt32(out uint health));
        Assert.Equal(60u, health);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Packet_CountsItsBlocks()
    {
        UpdateData data = new();
        Assert.True(data.IsEmpty);

        data.AddBlock([1, 2, 3]);
        data.AddBlock([4, 5, 6]);

        byte[] payload = data.BuildPayload();
        PacketReader reader = new(payload);

        Assert.True(reader.TryReadUInt32(out uint count));
        Assert.Equal(2u, count);
        Assert.Equal(6, reader.Remaining);
    }

    /// <summary>
    /// Departed objects share one block covering all of them, so the count is <c>blocks + 1</c>
    /// rather than <c>blocks + guids</c>.
    /// </summary>
    [Fact]
    public void OutOfRangeObjects_AreOneBlockRegardlessOfCount()
    {
        UpdateData data = new();
        data.AddBlock([9]);
        data.AddOutOfRange(ObjectGuid.Create(HighGuid.Unit, 1, 1));
        data.AddOutOfRange(ObjectGuid.Create(HighGuid.Unit, 1, 2));

        PacketReader reader = new(data.BuildPayload());

        Assert.True(reader.TryReadUInt32(out uint count));
        Assert.Equal(2u, count);

        Assert.True(reader.TryReadUInt8(out byte type));
        Assert.Equal((byte)UpdateType.OutOfRange, type);

        Assert.True(reader.TryReadUInt32(out uint guidCount));
        Assert.Equal(2u, guidCount);
    }

    [Fact]
    public void SmallPayloads_AreNotCompressed()
    {
        byte[] payload = new byte[UpdateData.CompressionThreshold];

        Assert.False(UpdateData.TryCompress(payload, out byte[] result));
        Assert.Equal(payload, result);
    }

    /// <summary>
    /// Over the threshold the payload is deflated with its uncompressed size in front, and the
    /// caller must switch to <c>SMSG_COMPRESSED_UPDATE_OBJECT</c>.
    /// </summary>
    [Fact]
    public void LargePayloads_AreDeflatedWithTheirSizeInFront()
    {
        byte[] payload = new byte[500];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 7);
        }

        Assert.True(UpdateData.TryCompress(payload, out byte[] compressed));

        PacketReader reader = new(compressed);
        Assert.True(reader.TryReadUInt32(out uint uncompressedSize));
        Assert.Equal(500u, uncompressedSize);

        using MemoryStream source = new(compressed, 4, compressed.Length - 4);
        using ZLibStream inflate = new(source, CompressionMode.Decompress);
        using MemoryStream output = new();
        inflate.CopyTo(output);

        Assert.Equal(payload, output.ToArray());
    }

    private static float ReadFloat(ref PacketReader reader)
    {
        Assert.True(reader.TryReadUInt32(out uint bits));
        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>
    /// Whether a 32-bit value appears anywhere in a block, at any alignment.
    /// </summary>
    /// <remarks>
    /// Deliberately not aligned to field boundaries: the point of the check is that the value is
    /// <i>absent</i>, and a search that only looked where the field ought to be would pass on a
    /// block that leaked it somewhere else.
    /// </remarks>
    private static bool Contains(byte[] block, uint value) =>
        block.AsSpan().IndexOf(BitConverter.GetBytes(value)) >= 0;
}
