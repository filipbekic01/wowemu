namespace WowEmu.Protocol;

/// <summary>
/// Which update fields a block carries.
/// </summary>
/// <remarks>
/// Port of <c>UpdateMask.h</c>. On the wire this is a byte block count followed by that many
/// 32-bit words, bit <c>i</c> of word <c>n</c> meaning "field <c>32n + i</c> follows in the value
/// list".
/// <para>
/// The block count is a <b>byte</b>, and a player has 1326 fields — 42 blocks — so it fits, but
/// only just. The values that follow are in ascending field order with no indices attached, so the
/// mask is the only thing telling the client which value is which: one bit wrong and every value
/// after it lands in the wrong field.
/// </para>
/// </remarks>
public sealed class UpdateMask
{
    /// <summary>Bits per block. The client reads the mask as 32-bit words.</summary>
    public const int BitsPerBlock = 32;

    private readonly bool[] _bits;

    public UpdateMask(int fieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fieldCount);

        FieldCount = fieldCount;
        BlockCount = (fieldCount + BitsPerBlock - 1) / BitsPerBlock;
        _bits = new bool[BlockCount * BitsPerBlock];
    }

    public int FieldCount { get; }

    /// <summary>Number of 32-bit words the mask occupies on the wire.</summary>
    public int BlockCount { get; }

    /// <summary>How many fields are set — i.e. how many values follow the mask.</summary>
    public int SetCount
    {
        get
        {
            int count = 0;

            foreach (bool bit in _bits)
            {
                if (bit)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public void Set(int index) => _bits[index] = true;

    public void Unset(int index) => _bits[index] = false;

    public bool IsSet(int index) => _bits[index];

    public void Clear() => Array.Clear(_bits);

    /// <summary>Keeps only the bits also set in <paramref name="other"/>.</summary>
    /// <remarks>
    /// This is how per-observer visibility is applied: build the full mask, then intersect it with
    /// the fields this particular observer is allowed to see.
    /// </remarks>
    public void IntersectWith(UpdateMask other)
    {
        ArgumentNullException.ThrowIfNull(other);

        for (int i = 0; i < _bits.Length; i++)
        {
            _bits[i] = _bits[i] && i < other._bits.Length && other._bits[i];
        }
    }

    /// <summary>Writes the block count and the packed mask words.</summary>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt8((byte)BlockCount);

        for (int block = 0; block < BlockCount; block++)
        {
            uint word = 0;

            for (int bit = 0; bit < BitsPerBlock; bit++)
            {
                if (_bits[(block * BitsPerBlock) + bit])
                {
                    word |= 1u << bit;
                }
            }

            writer.WriteUInt32(word);
        }
    }
}
