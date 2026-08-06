using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WowEmu.Tests.Integration;

/// <summary>
/// The client half of the 3.3.5a SRP6 handshake, written against the protocol rather than against
/// <c>WowEmu.Cryptography</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberate reimplementation. If the test drove the handshake with the same code the
/// server uses, the two halves would agree on any shared mistake and the test would prove only that
/// the code is self-consistent. It is the same reasoning that keeps the crypto vectors in
/// <c>tools/vectors/</c> out of C#, and it is a direct transcription of the client half in
/// <c>tools/harness/m1_login.py</c>.
/// </para>
/// <para>
/// Everything on the wire is little-endian unsigned, which is why every conversion goes through
/// <see cref="ToBytes"/> / <see cref="ToNumber"/> rather than <c>BigInteger.ToByteArray()</c> —
/// the framework's own encoding is two's-complement and will happily hand back 33 bytes.
/// </para>
/// </remarks>
internal static class SrpClient
{
    /// <summary>The 3.3.5a safe-prime modulus. Hard-coded in the client.</summary>
    public static readonly BigInteger N = BigInteger.Parse(
        "0894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7",
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture);

    public static readonly BigInteger G = 7;

    /// <summary>SRP's <c>k</c>. Fixed at 3 in this version rather than derived from N and g.</summary>
    public static readonly BigInteger Multiplier = 3;

    /// <summary>Little-endian unsigned, left-padded to <paramref name="length"/>.</summary>
    public static byte[] ToBytes(BigInteger value, int length)
    {
        byte[] raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);

        if (raw.Length == length)
        {
            return raw;
        }

        if (raw.Length > length)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"does not fit in {length} bytes");
        }

        byte[] padded = new byte[length];
        raw.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>Little-endian unsigned.</summary>
    public static BigInteger ToNumber(ReadOnlySpan<byte> bytes) =>
        new(bytes, isUnsigned: true, isBigEndian: false);

    public static byte[] Sha1(params byte[][] parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        foreach (byte[] part in parts)
        {
            hash.AppendData(part);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>
    /// <c>SHA1Interleave</c>: the 32-byte shared secret becomes the 40-byte session key.
    /// </summary>
    /// <remarks>
    /// The leading-zero strip is the whole point. Skipping it produces the right answer about 255
    /// times in 256 and an unreproducible login failure the other time — which is why
    /// <c>tools/vectors/vectors.json</c> carries a handshake constructed so that S starts with a
    /// zero byte.
    /// </remarks>
    public static byte[] Interleave(byte[] sharedSecret)
    {
        byte[] even = new byte[16];
        byte[] odd = new byte[16];

        for (int index = 0; index < 16; index++)
        {
            even[index] = sharedSecret[index * 2];
            odd[index] = sharedSecret[(index * 2) + 1];
        }

        int offset = 0;

        while (offset < 32 && sharedSecret[offset] == 0)
        {
            offset++;
        }

        if ((offset & 1) != 0)
        {
            offset++;
        }

        offset /= 2;

        byte[] hashEven = Sha1(even[offset..]);
        byte[] hashOdd = Sha1(odd[offset..]);

        byte[] sessionKey = new byte[40];

        for (int index = 0; index < 40; index++)
        {
            sessionKey[index] = index % 2 == 0 ? hashEven[index / 2] : hashOdd[index / 2];
        }

        return sessionKey;
    }

    /// <summary>
    /// The private exponent <c>a</c>. 19 bytes, as the client uses — not 32, so that a server which
    /// assumed a fixed width would be caught here.
    /// </summary>
    public static BigInteger GeneratePrivateEphemeral() =>
        ToNumber(RandomNumberGenerator.GetBytes(19));

    /// <summary>Uppercased and UTF-8 encoded, which is what the verifier was derived from.</summary>
    public static byte[] Normalize(string text) =>
        Encoding.UTF8.GetBytes(text.ToUpperInvariant());
}
