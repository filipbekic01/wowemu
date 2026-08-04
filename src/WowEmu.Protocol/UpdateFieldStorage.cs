using System.Buffers.Binary;
using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>
/// An object's update fields: a flat <c>uint[]</c> plus a record of which entries have changed.
/// </summary>
/// <remarks>
/// This is the whole of an object's client-visible state. Health, position-independent flags,
/// display id, every stat — all of it lives here as 32-bit words at the indices in
/// <see cref="UpdateFields"/>, because that is how the client reads it.
/// <para>
/// The dirty flags are a byte per field rather than a packed bitmask, matching upstream. A byte per
/// field costs 1326 bytes per player and makes set/clear a single store with no read-modify-write —
/// the mask is only packed into bits when a packet is built, which is far rarer than touching a
/// field.
/// </para>
/// <para>
/// <b>Two field types do not fit a 32-bit slot and must not be written as one.</b> A
/// <see cref="UpdateFieldType.Long"/> (a guid) spans two consecutive indices, and
/// <see cref="UpdateFieldType.Bytes"/> packs four independent values into one. Writing either with
/// a plain <see cref="SetUInt32"/> leaves half the value stale, and the client renders something
/// that looks almost right.
/// </para>
/// </remarks>
public sealed class UpdateFieldStorage
{
    private readonly uint[] _values;
    private readonly bool[] _dirty;

    /// <summary>Creates storage for <paramref name="fieldCount"/> fields, all zero.</summary>
    /// <param name="fieldCount">Usually one of the <c>*_END</c> constants.</param>
    public UpdateFieldStorage(int fieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldCount);

        _values = new uint[fieldCount];
        _dirty = new bool[fieldCount];
    }

    /// <summary>How many fields this object has.</summary>
    public int FieldCount => _values.Length;

    /// <summary>Whether anything has changed since the last <see cref="ClearDirty"/>.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>The raw values, for serialization.</summary>
    public ReadOnlySpan<uint> Values => _values;

    public uint GetUInt32(int index) => _values[index];

    public int GetInt32(int index) => unchecked((int)_values[index]);

    public float GetFloat(int index) => BitConverter.UInt32BitsToSingle(_values[index]);

    /// <summary>Reads one of the four bytes packed into a field.</summary>
    public byte GetByte(int index, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)offset, 4u);

        return (byte)((_values[index] >> (offset * 8)) & 0xFF);
    }

    /// <summary>Reads one of the two 16-bit halves of a field.</summary>
    public ushort GetUInt16(int index, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)offset, 2u);

        return (ushort)((_values[index] >> (offset * 16)) & 0xFFFF);
    }

    /// <summary>Reads a guid, which occupies this index and the next.</summary>
    public ObjectGuid GetGuid(int index) =>
        new(_values[index] | ((ulong)_values[index + 1] << 32));

    /// <summary>Sets a field. No-op — and not marked dirty — if the value is unchanged.</summary>
    /// <remarks>
    /// Skipping unchanged writes is not just an optimization: a field marked dirty is re-sent to
    /// every observer, so a system that rewrites the same value every tick would broadcast a full
    /// update every tick to everyone nearby.
    /// </remarks>
    public void SetUInt32(int index, uint value)
    {
        if (_values[index] == value)
        {
            return;
        }

        _values[index] = value;
        _dirty[index] = true;
        IsDirty = true;
    }

    public void SetInt32(int index, int value) => SetUInt32(index, unchecked((uint)value));

    public void SetFloat(int index, float value) => SetUInt32(index, BitConverter.SingleToUInt32Bits(value));

    /// <summary>Writes one of the four bytes packed into a field, leaving the others alone.</summary>
    public void SetByte(int index, int offset, byte value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)offset, 4u);

        int shift = offset * 8;
        uint updated = (_values[index] & ~(0xFFu << shift)) | ((uint)value << shift);

        SetUInt32(index, updated);
    }

    /// <summary>Writes one 16-bit half of a field, leaving the other alone.</summary>
    public void SetUInt16(int index, int offset, ushort value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)offset, 2u);

        int shift = offset * 16;
        uint updated = (_values[index] & ~(0xFFFFu << shift)) | ((uint)value << shift);

        SetUInt32(index, updated);
    }

    /// <summary>
    /// Writes a guid across this index and the next.
    /// </summary>
    /// <remarks>
    /// Both halves are always written, but each is dirtied only if it actually changed. A player
    /// guid's high word is 0x0000, so assigning one to a zeroed field legitimately dirties only the
    /// low half — the client already has zero in the other, and re-sending it would be noise.
    /// </remarks>
    public void SetGuid(int index, ObjectGuid objectGuid)
    {
        SetUInt32(index, (uint)(objectGuid.Value & 0xFFFFFFFF));
        SetUInt32(index + 1, (uint)(objectGuid.Value >> 32));
    }

    /// <summary>Turns individual bits on in a flags field.</summary>
    public void SetFlag(int index, uint flags) => SetUInt32(index, _values[index] | flags);

    /// <summary>Turns individual bits off in a flags field.</summary>
    public void RemoveFlag(int index, uint flags) => SetUInt32(index, _values[index] & ~flags);

    public bool HasFlag(int index, uint flags) => (_values[index] & flags) != 0;

    /// <summary>Whether a field has changed since the last <see cref="ClearDirty"/>.</summary>
    public bool IsFieldDirty(int index) => _dirty[index];

    /// <summary>
    /// Forgets every pending change. Called once the changes have been sent.
    /// </summary>
    public void ClearDirty()
    {
        Array.Clear(_dirty);
        IsDirty = false;
    }

    /// <summary>Writes the raw values of the fields selected by <paramref name="mask"/>.</summary>
    internal void WriteSelected(PacketWriter writer, UpdateMask mask)
    {
        for (int i = 0; i < _values.Length; i++)
        {
            if (mask.IsSet(i))
            {
                writer.WriteUInt32(_values[i]);
            }
        }
    }

    /// <summary>Builds a mask of every field that has changed.</summary>
    internal UpdateMask BuildDirtyMask()
    {
        UpdateMask mask = new(_values.Length);

        for (int i = 0; i < _dirty.Length; i++)
        {
            if (_dirty[i])
            {
                mask.Set(i);
            }
        }

        return mask;
    }

    /// <summary>
    /// Builds a mask of every field that is non-zero.
    /// </summary>
    /// <remarks>
    /// This is what a create block sends, not the dirty mask: a newly created object has no history
    /// with the observer, so everything it has must go out. Zero fields are skipped because the
    /// client starts them at zero anyway.
    /// </remarks>
    internal UpdateMask BuildCreateMask()
    {
        UpdateMask mask = new(_values.Length);

        for (int i = 0; i < _values.Length; i++)
        {
            if (_values[i] != 0)
            {
                mask.Set(i);
            }
        }

        return mask;
    }

    /// <summary>Reads a field straight out of a serialized value block. Tests and diagnostics.</summary>
    public static uint ReadRawField(ReadOnlySpan<byte> values, int ordinal) =>
        BinaryPrimitives.ReadUInt32LittleEndian(values[(ordinal * 4)..]);
}
