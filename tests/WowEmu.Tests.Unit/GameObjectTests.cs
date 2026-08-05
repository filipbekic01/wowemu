using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Building a gameobject, and the create block it produces.
/// </summary>
/// <remarks>
/// A gameobject's create block is a different shape from a unit's — no movement, no speeds, a packed
/// rotation, and a field block ending at slot 18 rather than 148. Nothing in it carries a length, so
/// a section that is the wrong size does not degrade the picture; it shifts every byte after it and
/// the client drops the connection.
/// </remarks>
public sealed class GameObjectTests
{
    [Fact]
    public void Guid_IsAGameObjectGuidCarryingTheEntry()
    {
        GameObject gameObject = Build();

        Assert.Equal(HighGuid.GameObject, gameObject.Guid.High);
        Assert.Equal(SampleEntry, gameObject.Guid.Entry);
        Assert.Equal(SampleSpawnId, gameObject.Guid.Counter);
    }

    /// <summary>It is an object, not a unit — no level, no health, no unit bit.</summary>
    [Fact]
    public void TypeMask_IsObjectAndGameObject()
    {
        GameObject gameObject = Build();

        Assert.Equal(TypeId.GameObject, gameObject.TypeId);

        uint mask = gameObject.Fields.GetUInt32(UpdateFields.OBJECT_FIELD_TYPE);
        Assert.Equal(TypeMask.Object | TypeMask.GameObject, mask);
        Assert.Equal(0u, mask & TypeMask.Unit);
    }

    /// <summary>GAMEOBJECT_BYTES_1 packs state, type, art kit and animation into one slot.</summary>
    [Fact]
    public void Bytes1_PacksStateTypeAndAnimation()
    {
        GameObject gameObject = Build(
            spawn: Spawn() with { State = 1, AnimProgress = 100 },
            template: Template() with { Type = 3 });

        Assert.Equal(1, gameObject.GoState);
        Assert.Equal(3, gameObject.GoType);
        Assert.Equal(100, gameObject.Fields.GetByte(UpdateFields.GAMEOBJECT_BYTES_1, 3));
    }

    [Fact]
    public void TemplateFields_ReachTheUpdateFields()
    {
        GameObject gameObject = Build(template: Template() with
        {
            DisplayId = 259,
            Faction = 114,
            Flags = 0x20,
            Size = 1.5f,
        });

        Assert.Equal(259u, gameObject.DisplayId);
        Assert.Equal(114u, gameObject.Fields.GetUInt32(UpdateFields.GAMEOBJECT_FACTION));
        Assert.Equal(0x20u, gameObject.Fields.GetUInt32(UpdateFields.GAMEOBJECT_FLAGS));
        Assert.Equal(1.5f, gameObject.Fields.GetFloat(UpdateFields.OBJECT_FIELD_SCALE_X), 0.0001f);
        Assert.Equal(SampleEntry, gameObject.Fields.GetUInt32(UpdateFields.OBJECT_FIELD_ENTRY));
    }

    /// <summary>
    /// An all-zero quaternion falls back to a rotation about the vertical axis by the object's own
    /// facing.
    /// </summary>
    /// <remarks>
    /// This is not an edge case: 15,478 of the 85,552 rows in <c>gameobject</c> carry one. Without
    /// the fallback, normalising divides by zero and the packed rotation becomes NaN-derived
    /// garbage — for 18 % of every gameobject in the world.
    /// </remarks>
    [Fact]
    public void AZeroQuaternion_FallsBackToTheObjectsFacing()
    {
        ulong packed = UpdateBlockBuilder.PackRotation(0f, 0f, 0f, 0f, orientation: MathF.PI);

        // A half-turn about Z is (0, 0, 1, 0): z packs to its full positive scale, x and y to zero.
        ulong fromExplicitQuaternion = UpdateBlockBuilder.PackRotation(0f, 0f, 1f, 0f, orientation: 0f);

        Assert.Equal(fromExplicitQuaternion, packed);
        Assert.NotEqual(0ul, packed);
    }

    /// <summary>A zero rotation with zero facing is the identity, which packs to zero.</summary>
    [Fact]
    public void AZeroQuaternionWithNoFacing_PacksToIdentity()
    {
        Assert.Equal(0ul, UpdateBlockBuilder.PackRotation(0f, 0f, 0f, 0f, orientation: 0f));
    }

    /// <summary>
    /// The sign of w flips x, y and z.
    /// </summary>
    /// <remarks>
    /// Upstream multiplies each component by the sign of w before masking, which is how it fits a
    /// four-component rotation into three packed fields — w is recovered from the others. Dropping
    /// the sign leaves half of all rotations mirrored.
    /// </remarks>
    [Fact]
    public void TheSignOfW_FlipsTheOtherComponents()
    {
        ulong positive = UpdateBlockBuilder.PackRotation(0.5f, 0f, 0f, 0.866f, 0f);
        ulong negative = UpdateBlockBuilder.PackRotation(0.5f, 0f, 0f, -0.866f, 0f);

        Assert.NotEqual(positive, negative);
    }

    /// <summary>Each of the three components occupies its own 21-bit field.</summary>
    [Fact]
    public void Components_PackIntoTheirOwnBitRanges()
    {
        // Only z is non-zero, so nothing above bit 21 may be set.
        ulong onlyZ = UpdateBlockBuilder.PackRotation(0f, 0f, 1f, 0.0001f, 0f);
        Assert.Equal(0ul, onlyZ >> 21);

        // Only y is non-zero, so bits 0-20 must be clear and something in 21-41 set.
        ulong onlyY = UpdateBlockBuilder.PackRotation(0f, 1f, 0f, 0.0001f, 0f);
        Assert.Equal(0ul, onlyY & 0x1FFFFF);
        Assert.NotEqual(0ul, (onlyY >> 21) & 0x1FFFFF);
    }

    /// <summary>
    /// The create block, walked section by section against <c>BuildMovementUpdate</c>.
    /// </summary>
    /// <remarks>
    /// The trap here is that a gameobject carries <b>both</b> position flags, and upstream tests
    /// them as if/else — so the <c>POSITION</c> branch runs and the stationary four floats are never
    /// written. Writing both is the obvious reading of the flags and makes the block sixteen bytes
    /// too long.
    /// </remarks>
    [Fact]
    public void CreateBlock_HasEverySectionInOrder()
    {
        GameObject gameObject = Build();

        byte[] block = UpdateBlockBuilder.BuildGameObjectCreateBlock(
            gameObject.Guid, gameObject.Fields, gameObject.Position, gameObject.PackedRotation);

        PacketReader reader = new(block);

        Assert.True(reader.TryReadUInt8(out byte updateType));
        Assert.Equal((byte)UpdateType.CreateObject, updateType);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid readGuid));
        Assert.Equal(gameObject.Guid, readGuid);

        Assert.True(reader.TryReadUInt8(out byte typeId));
        Assert.Equal((byte)TypeId.GameObject, typeId);

        Assert.True(reader.TryReadUInt16(out ushort flags));
        Assert.Equal(
            (ushort)(UpdateFlag.LowGuid | UpdateFlag.StationaryPosition
                | UpdateFlag.Position | UpdateFlag.Rotation),
            flags);

        // Position branch: an empty transport guid, then the position twice, orientation, and a zero.
        Assert.True(reader.TryReadUInt8(out byte transportGuid));
        Assert.Equal(0, transportGuid);

        Assert.Equal(SampleX, ReadFloat(ref reader), 0.001f);
        Assert.Equal(SampleY, ReadFloat(ref reader), 0.001f);
        Assert.Equal(SampleZ, ReadFloat(ref reader), 0.001f);

        // The repeat is the transport-relative offset; with no transport upstream writes the world
        // position again rather than zeroes.
        Assert.Equal(SampleX, ReadFloat(ref reader), 0.001f);
        Assert.Equal(SampleY, ReadFloat(ref reader), 0.001f);
        Assert.Equal(SampleZ, ReadFloat(ref reader), 0.001f);

        Assert.Equal(SampleOrientation, ReadFloat(ref reader), 0.001f);
        Assert.Equal(0f, ReadFloat(ref reader), 0.001f);       // corpse orientation, zero for anything else

        // LowGuid: a gameobject sends its real counter, unlike a unit or a player.
        Assert.True(reader.TryReadUInt32(out uint lowGuid));
        Assert.Equal(SampleSpawnId, lowGuid);

        Assert.True(reader.TryReadUInt64(out ulong rotation));
        Assert.Equal(gameObject.PackedRotation, rotation);

        // The field block is 18 slots, so the mask is one word.
        Assert.True(reader.TryReadUInt8(out byte blockCount));
        Assert.Equal((UpdateFields.GAMEOBJECT_END + 31) / 32, blockCount);

        for (int i = 0; i < blockCount; i++)
        {
            Assert.True(reader.TryReadUInt32(out _));
        }

        while (reader.Remaining > 0)
        {
            Assert.True(reader.TryReadUInt32(out _));
        }

        Assert.True(reader.Ok);
    }

    /// <summary>A gameobject's block must be far shorter than a unit's; they end at different slots.</summary>
    [Fact]
    public void CreateBlock_IsMuchShorterThanAUnitsWouldBe()
    {
        GameObject gameObject = Build();

        byte[] block = UpdateBlockBuilder.BuildGameObjectCreateBlock(
            gameObject.Guid, gameObject.Fields, gameObject.Position, gameObject.PackedRotation);

        // One mask word against a unit's five, and no movement block or nine speeds.
        Assert.True(block.Length < 128, $"gameobject create block was {block.Length} bytes");
    }

    private const uint SampleEntry = 1731;
    private const uint SampleSpawnId = 4242;
    private const float SampleX = -8913.23f;
    private const float SampleY = 554.63f;
    private const float SampleZ = 93.79f;
    private const float SampleOrientation = 1.0472f;

    private static GameObject Build(GameObjectSpawn? spawn = null, GameObjectTemplate? template = null) =>
        GameObject.Create(spawn ?? Spawn(), template ?? Template());

    private static GameObjectSpawn Spawn() => new(
        SpawnId: SampleSpawnId,
        Entry: SampleEntry,
        MapId: 0,
        SpawnMask: 1,
        PhaseMask: 1,
        Position: new Position(SampleX, SampleY, SampleZ, SampleOrientation),
        Rotation0: 0f,
        Rotation1: 0f,
        Rotation2: 0.5f,
        Rotation3: 0.866f,
        AnimProgress: 100,
        State: 1);

    private static GameObjectTemplate Template() => new(
        Entry: SampleEntry,
        Type: 2,
        DisplayId: 259,
        Name: "Copper Vein",
        Faction: 0,
        Flags: 0,
        Size: 1.0f);

    private static float ReadFloat(ref PacketReader reader)
    {
        Assert.True(reader.TryReadUInt32(out uint bits));
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
