using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WowEmu.Cryptography;

/// <summary>
/// Server side of the SRP6 handshake used by the 3.3.5a logon server.
/// </summary>
/// <remarks>
/// Port of <c>src/common/Cryptography/Authentication/SRP6.{h,cpp}</c>.
/// <para>
/// <b>Every</b> value here is a fixed-width, little-endian, zero-padded byte array — that is what
/// OpenSSL's <c>BN_bn2lebinpad</c> produces and what goes on the wire. <see cref="BigInteger"/> must
/// therefore always be converted with <c>isUnsigned: true, isBigEndian: false</c>; its default
/// two's-complement behaviour would turn any 32-byte value with the high bit set into a negative
/// number and silently break the M1 comparison.
/// </para>
/// <para>
/// <b>Callers must uppercase the username and password with the server's Latin-only uppercasing
/// first.</b> The stored verifier was computed over that exact transform, so
/// <c>string.ToUpperInvariant</c> is not a substitute for accounts with accented characters.
/// </para>
/// </remarks>
public sealed class Srp6
{
    /// <summary>Length of the random per-account salt, in bytes.</summary>
    public const int SaltLength = 32;

    /// <summary>Length of the password verifier, in bytes.</summary>
    public const int VerifierLength = 32;

    /// <summary>Length of the ephemeral keys A and B, in bytes.</summary>
    public const int EphemeralKeyLength = 32;

    /// <summary>Length of the derived session key, in bytes.</summary>
    public const int SessionKeyLength = 40;

    /// <summary>Length of a SHA-1 digest, in bytes.</summary>
    public const int DigestLength = 20;

    /// <summary>
    /// The SRP6 modulus, as written in AzerothCore's source. Kept in this form so the constant is
    /// greppable against upstream; <see cref="N"/> is the little-endian byte form that goes on
    /// the wire.
    /// </summary>
    private const string NHex = "894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7";

    // Declaration order matters: the byte arrays feed the BigInteger initializers below.
    private static readonly byte[] NBytes = ParseHexReversed(NHex);
    private static readonly byte[] GBytes = [7];
    private static readonly BigInteger NValue = FromLittleEndian(NBytes);
    private static readonly BigInteger GValue = FromLittleEndian(GBytes);

    private readonly byte[] _i;
    private readonly BigInteger _b;
    private readonly byte[] _bBytes;
    private readonly BigInteger _v;
    private bool _used;

    /// <summary>Creates a challenge for one login attempt, with a fresh random secret <c>b</c>.</summary>
    public Srp6(string username, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> verifier)
        : this(username, salt, verifier, RandomNumberGenerator.GetBytes(EphemeralKeyLength))
    {
    }

    /// <summary>Creates a challenge with a caller-supplied <c>b</c>. Tests only.</summary>
    internal Srp6(string username, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> verifier, ReadOnlySpan<byte> b)
    {
        ArgumentNullException.ThrowIfNull(username);

        if (salt.Length != SaltLength)
        {
            throw new ArgumentException($"Salt must be {SaltLength} bytes.", nameof(salt));
        }

        if (verifier.Length != VerifierLength)
        {
            throw new ArgumentException($"Verifier must be {VerifierLength} bytes.", nameof(verifier));
        }

        _i = SHA1.HashData(Encoding.UTF8.GetBytes(username));
        _bBytes = b.ToArray();
        _b = FromLittleEndian(_bBytes);
        _v = FromLittleEndian(verifier);

        Salt = salt.ToArray();
        B = ComputeB(_b, _v);
    }

    /// <summary>The modulus N, little-endian — exactly the 32 bytes sent to the client.</summary>
    public static ReadOnlySpan<byte> N => NBytes;

    /// <summary>The generator g. One byte, value 7.</summary>
    public static ReadOnlySpan<byte> G => GBytes;

    /// <summary>The account's password salt.</summary>
    public byte[] Salt { get; }

    /// <summary>The server's public ephemeral key, <c>B = (g^b + 3v) mod N</c>.</summary>
    public byte[] B { get; }

    /// <summary>Generates a fresh salt and the matching verifier for a new account.</summary>
    public static (byte[] Salt, byte[] Verifier) MakeRegistrationData(string username, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        return (salt, CalculateVerifier(username, password, salt));
    }

    /// <summary>Computes <c>v = g ^ H(s || H(USER || ':' || PASS)) mod N</c>.</summary>
    public static byte[] CalculateVerifier(string username, string password, ReadOnlySpan<byte> salt)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        if (salt.Length != SaltLength)
        {
            throw new ArgumentException($"Salt must be {SaltLength} bytes.", nameof(salt));
        }

        Span<byte> inner = stackalloc byte[DigestLength];
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(username));
            hash.AppendData(":"u8);
            hash.AppendData(Encoding.UTF8.GetBytes(password));
            hash.GetHashAndReset(inner);
        }

        Span<byte> x = stackalloc byte[DigestLength];
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            hash.AppendData(salt);
            hash.AppendData(inner);
            hash.GetHashAndReset(x);
        }

        return ToLittleEndian(BigInteger.ModPow(GValue, FromLittleEndian(x), NValue), VerifierLength);
    }

    /// <summary>Recomputes the verifier and compares it against the stored one.</summary>
    public static bool CheckLogin(string username, string password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> verifier)
    {
        return CryptographicOperations.FixedTimeEquals(
            CalculateVerifier(username, password, salt), verifier);
    }

    /// <summary>Computes M2 = <c>H(A || M1 || K)</c>, the server's proof back to the client.</summary>
    public static byte[] GetSessionVerifier(ReadOnlySpan<byte> a, ReadOnlySpan<byte> clientM, ReadOnlySpan<byte> sessionKey)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(a);
        hash.AppendData(clientM);
        hash.AppendData(sessionKey);
        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Verifies the client's proof. Returns the 40-byte session key on success, or
    /// <see langword="null"/> if the proof does not match.
    /// </summary>
    /// <remarks>
    /// A single instance may only be used once — upstream asserts on reuse as an anti-replay guard.
    /// </remarks>
    public byte[]? VerifyChallengeResponse(ReadOnlySpan<byte> a, ReadOnlySpan<byte> clientM)
    {
        if (_used)
        {
            throw new InvalidOperationException(
                "A single Srp6 instance must only ever be used to verify once.");
        }

        _used = true;

        if (a.Length != EphemeralKeyLength)
        {
            return null;
        }

        BigInteger aValue = FromLittleEndian(a);
        if ((aValue % NValue).IsZero)
        {
            return null;
        }

        Span<byte> uDigest = stackalloc byte[DigestLength];
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            hash.AppendData(a);
            hash.AppendData(B);
            hash.GetHashAndReset(uDigest);
        }

        BigInteger u = FromLittleEndian(uDigest);
        BigInteger sValue = BigInteger.ModPow(aValue * BigInteger.ModPow(_v, u, NValue), _b, NValue);
        byte[] s = ToLittleEndian(sValue, EphemeralKeyLength);

        byte[] sessionKey = Sha1Interleave(s);

        // NgHash = H(N) xor H(g)
        Span<byte> ngHash = stackalloc byte[DigestLength];
        SHA1.HashData(NBytes, ngHash);
        Span<byte> gHash = stackalloc byte[DigestLength];
        SHA1.HashData(GBytes, gHash);
        for (int i = 0; i < DigestLength; i++)
        {
            ngHash[i] ^= gHash[i];
        }

        Span<byte> ourM = stackalloc byte[DigestLength];
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            hash.AppendData(ngHash);
            hash.AppendData(_i);
            hash.AppendData(Salt);
            hash.AppendData(a);
            hash.AppendData(B);
            hash.AppendData(sessionKey);
            hash.GetHashAndReset(ourM);
        }

        return CryptographicOperations.FixedTimeEquals(ourM, clientM) ? sessionKey : null;
    }

    /// <summary>
    /// Derives the 40-byte session key from the 32-byte shared secret S.
    /// </summary>
    /// <remarks>
    /// S is split into its even- and odd-indexed bytes, each half is hashed, and the two digests
    /// are interleaved back together.
    /// <para>
    /// <b>The leading-zero strip is load-bearing.</b> Hashing starts past any zero bytes at the
    /// front of S, with the offset rounded <i>up</i> to an even index before halving. A naive
    /// "split even/odd and hash both halves" implementation is wrong whenever S begins with a zero
    /// byte — roughly 1 login in 256 — and fails intermittently in a way that is very hard to trace.
    /// </para>
    /// </remarks>
    internal static byte[] Sha1Interleave(ReadOnlySpan<byte> s)
    {
        if (s.Length != EphemeralKeyLength)
        {
            throw new ArgumentException($"S must be {EphemeralKeyLength} bytes.", nameof(s));
        }

        const int Half = EphemeralKeyLength / 2;

        Span<byte> buf0 = stackalloc byte[Half];
        Span<byte> buf1 = stackalloc byte[Half];
        for (int i = 0; i < Half; i++)
        {
            buf0[i] = s[(2 * i) + 0];
            buf1[i] = s[(2 * i) + 1];
        }

        int p = 0;
        while (p < EphemeralKeyLength && s[p] == 0)
        {
            p++;
        }

        if ((p & 1) != 0)
        {
            p++; // skip one extra byte if p is odd
        }

        p /= 2; // offset into the halves

        Span<byte> hash0 = stackalloc byte[DigestLength];
        Span<byte> hash1 = stackalloc byte[DigestLength];
        SHA1.HashData(buf0[p..], hash0);
        SHA1.HashData(buf1[p..], hash1);

        byte[] sessionKey = new byte[SessionKeyLength];
        for (int i = 0; i < DigestLength; i++)
        {
            sessionKey[(2 * i) + 0] = hash0[i];
            sessionKey[(2 * i) + 1] = hash1[i];
        }

        return sessionKey;
    }

    /// <summary>B = <c>(g^b + 3v) mod N</c>, serialized to exactly 32 little-endian bytes.</summary>
    private static byte[] ComputeB(BigInteger b, BigInteger v)
    {
        BigInteger value = (BigInteger.ModPow(GValue, b, NValue) + (v * 3)) % NValue;
        return ToLittleEndian(value, EphemeralKeyLength);
    }

    /// <summary>Reads an unsigned little-endian integer. Equivalent to <c>BN_lebin2bn</c>.</summary>
    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes)
    {
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
    }

    /// <summary>
    /// Writes an unsigned little-endian integer, zero-padded to a fixed width.
    /// Equivalent to <c>BN_bn2lebinpad</c>.
    /// </summary>
    private static byte[] ToLittleEndian(BigInteger value, int length)
    {
        byte[] result = new byte[length];
        if (!value.TryWriteBytes(result, out _, isUnsigned: true, isBigEndian: false))
        {
            throw new InvalidOperationException(
                $"Value does not fit in {length} bytes; SRP6 values are fixed-width.");
        }

        return result;
    }

    /// <summary>
    /// Parses a hex string back-to-front, so the resulting array is the little-endian
    /// representation of the number the string spells out. Mirrors
    /// <c>Acore::Impl::HexStrToByteArray(..., reverse: true)</c>.
    /// </summary>
    private static byte[] ParseHexReversed(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            int source = hex.Length - (2 * (i + 1));
            result[i] = Convert.ToByte(hex.Substring(source, 2), 16);
        }

        return result;
    }
}
