using System.Buffers.Binary;
using System.IO.Compression;
using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>What an update block does to the client's copy of an object.</summary>
public enum UpdateType : byte
{
    /// <summary>Change fields on an object the client already has.</summary>
    Values = 0,

    /// <summary>Move an object the client already has.</summary>
    Movement = 1,

    /// <summary>Create an object the client has never seen.</summary>
    CreateObject = 2,

    /// <summary>
    /// Create, for objects the client tracks differently — players, corpses, dynamic objects, pets.
    /// </summary>
    CreateObject2 = 3,

    /// <summary>Destroy objects that have left the client's view.</summary>
    OutOfRange = 4,

    NearObjects = 5,
}

/// <summary>Object kinds, as the create block labels them.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "TYPEID_OBJECT is the client's name for the base object kind.")]
public enum TypeId : byte
{
    Object = 0,
    Item = 1,
    Container = 2,
    Unit = 3,
    Player = 4,
    GameObject = 5,
    DynamicObject = 6,
    Corpse = 7,
}

/// <summary>
/// Which optional sections a create block's movement data carries.
/// </summary>
/// <remarks>
/// Every flag here adds bytes at a fixed position. The client reads them in this exact order, so
/// setting a flag without writing its payload desynchronises the rest of the packet.
/// </remarks>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "These are the client's own update flags; the protocol calls them flags and so does every capture.")]
public enum UpdateFlag : ushort
{
    None = 0x0000,

    /// <summary>This object is the observer's own character.</summary>
    Self = 0x0001,

    Transport = 0x0002,
    HasTarget = 0x0004,
    Unknown = 0x0008,
    LowGuid = 0x0010,

    /// <summary>Carries a full movement block — anything that can move.</summary>
    Living = 0x0020,

    /// <summary>Carries a bare position and orientation, for things that cannot.</summary>
    StationaryPosition = 0x0040,

    Vehicle = 0x0080,
    Position = 0x0100,
    Rotation = 0x0200,
}

/// <summary>
/// Accumulates update blocks and builds <c>SMSG_UPDATE_OBJECT</c>.
/// </summary>
/// <remarks>
/// Port of <c>UpdateData.{h,cpp}</c>. One packet can carry many blocks about many objects, which is
/// how a client learns about a screen full of creatures in one message.
/// <para>
/// Above 100 bytes the packet is deflated and its opcode becomes
/// <c>SMSG_COMPRESSED_UPDATE_OBJECT</c>, with the uncompressed size as a <c>uint32</c> in front.
/// The threshold is upstream's; the client accepts either form at any size, but matching the
/// threshold keeps captures comparable.
/// </para>
/// </remarks>
public sealed class UpdateData
{
    /// <summary>Payload size above which the packet is compressed.</summary>
    public const int CompressionThreshold = 100;

    private readonly PacketWriter _blocks = new(512);
    private readonly List<ObjectGuid> _outOfRange = [];

    private int _blockCount;

    /// <summary>How many blocks have been added, excluding the out-of-range block.</summary>
    public int BlockCount => _blockCount;

    public bool IsEmpty => _blockCount == 0 && _outOfRange.Count == 0;

    /// <summary>Appends a prebuilt block.</summary>
    public void AddBlock(ReadOnlySpan<byte> block)
    {
        _blocks.WriteBytes(block);
        _blockCount++;
    }

    /// <summary>Marks an object as no longer visible, so the client destroys its copy.</summary>
    public void AddOutOfRange(ObjectGuid objectGuid) => _outOfRange.Add(objectGuid);

    /// <summary>
    /// Builds the packet body.
    /// </summary>
    /// <remarks>
    /// The out-of-range list is one block covering every departed object, which is why the count
    /// written is <c>blocks + 1</c> rather than <c>blocks + guids</c>.
    /// </remarks>
    public byte[] BuildPayload()
    {
        PacketWriter writer = new(_blocks.Length + 32);

        writer.WriteUInt32((uint)(_outOfRange.Count > 0 ? _blockCount + 1 : _blockCount));

        if (_outOfRange.Count > 0)
        {
            writer.WriteUInt8((byte)UpdateType.OutOfRange);
            writer.WriteUInt32((uint)_outOfRange.Count);

            foreach (ObjectGuid entry in _outOfRange)
            {
                writer.WritePackedGuid(entry);
            }
        }

        writer.WriteBytes(_blocks.WrittenSpan);

        return writer.ToArray();
    }

    /// <summary>
    /// Compresses a payload if it is over the threshold.
    /// </summary>
    /// <returns>
    /// True when the result is compressed and the caller must send
    /// <c>SMSG_COMPRESSED_UPDATE_OBJECT</c> instead of <c>SMSG_UPDATE_OBJECT</c>.
    /// </returns>
    /// <remarks>
    /// Our deflate output will not be byte-identical to upstream's — zlib's single-shot
    /// <c>deflate(Z_NO_FLUSH)</c> makes different block choices — but it is valid zlib and the
    /// client decompresses it the same. PLAN.md §9 excludes compressed bodies from byte-exact
    /// comparison for exactly this reason.
    /// </remarks>
    public static bool TryCompress(ReadOnlySpan<byte> payload, out byte[] result)
    {
        if (payload.Length <= CompressionThreshold)
        {
            result = payload.ToArray();
            return false;
        }

        using MemoryStream output = new();

        // The uncompressed size goes in front, uncompressed — the client sizes its buffer from it
        // before it starts inflating.
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        output.Write(size);

        using (ZLibStream deflate = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(payload);
        }

        result = output.ToArray();
        return true;
    }
}
