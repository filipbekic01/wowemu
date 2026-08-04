using System.Security.Cryptography;

namespace WowEmu.Cryptography;

/// <summary>
/// Expands the 40-byte SRP6 session key into an endless keystream, 20 bytes at a time.
/// </summary>
/// <remarks>
/// Port of <c>src/common/Cryptography/SessionKeyGenerator.h</c>, instantiated in AzerothCore only
/// with SHA-1. Warden uses it to derive its own pair of RC4 keys from the session key
/// (<c>WardenWin.cpp:117-124</c>: 16 bytes client-to-server, then 16 bytes server-to-client).
/// <para>
/// Given input <c>K</c>: <c>o1 = H(K[0..20])</c>, <c>o2 = H(K[20..40])</c>, and
/// <c>o0 = H(o1 || o0 || o2)</c> where <c>o0</c> starts as 20 zero bytes. Each refill re-runs
/// <c>o0 = H(o1 || o0 || o2)</c>.
/// </para>
/// </remarks>
public sealed class SessionKeyGenerator
{
    private const int DigestLength = 20;

    private readonly byte[] _o0 = new byte[DigestLength];
    private readonly byte[] _o1;
    private readonly byte[] _o2;
    private int _position;

    /// <summary>Seeds the generator from an arbitrary buffer (in practice, the session key).</summary>
    public SessionKeyGenerator(ReadOnlySpan<byte> seed)
    {
        int half = seed.Length / 2;
        _o1 = SHA1.HashData(seed[..half]);
        _o2 = SHA1.HashData(seed[half..]);

        // _o0 is still all zeros here, and it is part of the hash input.
        Refill();
    }

    /// <summary>Fills <paramref name="destination"/> with the next bytes of the keystream.</summary>
    public void Generate(Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            if (_position == DigestLength)
            {
                Refill();
            }

            destination[i] = _o0[_position++];
        }
    }

    /// <summary>Returns the next <paramref name="count"/> bytes of the keystream.</summary>
    public byte[] Generate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        byte[] result = new byte[count];
        Generate(result);
        return result;
    }

    private void Refill()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(_o1);
        hash.AppendData(_o0);
        hash.AppendData(_o2);
        hash.GetHashAndReset(_o0);
        _position = 0;
    }
}
