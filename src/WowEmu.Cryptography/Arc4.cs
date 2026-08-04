namespace WowEmu.Cryptography;

/// <summary>
/// RC4 stream cipher.
/// </summary>
/// <remarks>
/// .NET deliberately removed RC4 from <c>System.Security.Cryptography</c>, and AzerothCore only
/// reaches it through OpenSSL's <i>legacy</i> provider (<c>src/common/Cryptography/ARC4.cpp</c>),
/// so we ship our own.
/// <para>
/// The cipher is stateful and continuous across calls: the WotLK protocol keys one stream per
/// direction at session start and never re-keys. Encrypting and decrypting are the same operation.
/// </para>
/// </remarks>
public sealed class Arc4
{
    private readonly byte[] _state = new byte[256];
    private byte _i;
    private byte _j;

    /// <summary>Initializes the cipher state from <paramref name="key"/> (the RC4 KSA).</summary>
    public Arc4(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("RC4 key must not be empty.", nameof(key));
        }

        for (int i = 0; i < 256; i++)
        {
            _state[i] = (byte)i;
        }

        byte j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (byte)(j + _state[i] + key[i % key.Length]);
            (_state[i], _state[j]) = (_state[j], _state[i]);
        }
    }

    /// <summary>
    /// XORs <paramref name="data"/> with the next bytes of the keystream, in place, and advances
    /// the stream. Mirrors <c>ARC4::UpdateData</c>.
    /// </summary>
    public void Process(Span<byte> data)
    {
        byte i = _i;
        byte j = _j;
        byte[] s = _state;

        for (int n = 0; n < data.Length; n++)
        {
            i = (byte)(i + 1);
            j = (byte)(j + s[i]);
            (s[i], s[j]) = (s[j], s[i]);
            data[n] ^= s[(byte)(s[i] + s[j])];
        }

        _i = i;
        _j = j;
    }

    /// <summary>
    /// Discards <paramref name="count"/> bytes of keystream. WoW uses ARC4-drop1024.
    /// </summary>
    public void Drop(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        // Same code path as Process over zeroed scratch, so drop and encrypt cannot diverge.
        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            int chunk = Math.Min(count, scratch.Length);
            scratch[..chunk].Clear();
            Process(scratch[..chunk]);
            count -= chunk;
        }
    }
}
