using WowEmu.Cryptography;

namespace WowEmu.Tests.Unit;

public sealed class SessionKeyGeneratorTests
{
    private static readonly byte[] Seed = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f2021222324252627");

    /// <summary>
    /// 64 bytes crosses three refills, so this pins both the initial
    /// <c>o0 = H(o1 || zeroes || o2)</c> and the <c>o0 = H(o1 || o0 || o2)</c> recurrence.
    /// </summary>
    [Fact]
    public void Generate_MatchesReference()
    {
        SessionKeyGenerator generator = new(Seed);

        byte[] output = generator.Generate(64);

        Assert.Equal(
            "e711c90e3921b9725e3ccc867309995596d79bec6044480918ee080579d8901a"
            + "849ac16df7e3e4402c97ef7f3d9275b31b5c9ff76ec817c344d98d3413447967",
            Convert.ToHexString(output).ToLowerInvariant());
    }

    /// <summary>
    /// Warden pulls 16 bytes for the client-to-server RC4 key and then 16 more for the
    /// server-to-client key, so the stream must survive being drawn in pieces that straddle the
    /// 20-byte digest boundary.
    /// </summary>
    [Fact]
    public void Generate_IsContinuousAcrossCalls()
    {
        byte[] wholeAtOnce = new SessionKeyGenerator(Seed).Generate(64);

        SessionKeyGenerator piecewiseGenerator = new(Seed);
        byte[] piecewise = new byte[64];
        piecewiseGenerator.Generate(piecewise.AsSpan(0, 16));  // Warden C->S key
        piecewiseGenerator.Generate(piecewise.AsSpan(16, 16)); // Warden S->C key
        piecewiseGenerator.Generate(piecewise.AsSpan(32, 1));  // crosses the digest boundary
        piecewiseGenerator.Generate(piecewise.AsSpan(33, 31));

        Assert.Equal(wholeAtOnce, piecewise);
    }

    [Fact]
    public void Generate_IsDeterministicForTheSameSeed()
    {
        Assert.Equal(
            new SessionKeyGenerator(Seed).Generate(40),
            new SessionKeyGenerator(Seed).Generate(40));
    }

    [Fact]
    public void Generate_DiffersForDifferentSeeds()
    {
        byte[] other = (byte[])Seed.Clone();
        other[0] ^= 0xFF;

        Assert.NotEqual(
            new SessionKeyGenerator(Seed).Generate(40),
            new SessionKeyGenerator(other).Generate(40));
    }

    [Fact]
    public void Generate_RejectsNegativeCount()
    {
        SessionKeyGenerator generator = new(Seed);
        Assert.Throws<ArgumentOutOfRangeException>(() => generator.Generate(-1));
    }
}
