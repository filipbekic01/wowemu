using WowEmu.Cryptography;

namespace WowEmu.Tests.Unit;

public sealed class Arc4Tests
{
    // RFC 6229 test vectors. These are the authoritative published RC4 vectors, so they validate
    // our implementation independently of anything in AzerothCore.
    [Theory]
    [InlineData("0102030405", "b2396305f03dc027ccc3524a0a1118a8")]
    [InlineData("0102030405060708090a0b0c0d0e0f10", "9ac7cc9a609d1ef7b2932899cde41b97")]
    public void Keystream_MatchesRfc6229_AtOffsetZero(string keyHex, string expectedHex)
    {
        Arc4 cipher = new(Convert.FromHexString(keyHex));

        byte[] keystream = new byte[16];
        cipher.Process(keystream); // XOR against zeros == raw keystream

        Assert.Equal(expectedHex, Convert.ToHexString(keystream).ToLowerInvariant());
    }

    // RFC 6229 also publishes the keystream at offset 1024, which is exactly where WoW's
    // ARC4-drop1024 starts.
    [Fact]
    public void Drop1024_MatchesRfc6229_AtOffset1024()
    {
        Arc4 cipher = new(Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));
        cipher.Drop(1024);

        byte[] keystream = new byte[16];
        cipher.Process(keystream);

        Assert.Equal("bdf0324e6083dcc6d3cedd3ca8c53c16", Convert.ToHexString(keystream).ToLowerInvariant());
    }

    [Fact]
    public void Drop_IsEquivalentToProcessingZeroes()
    {
        byte[] key = Convert.FromHexString("0102030405060708090a0b0c0d0e0f10");

        Arc4 dropped = new(key);
        dropped.Drop(1024);

        Arc4 manual = new(key);
        manual.Process(new byte[1024]);

        byte[] a = new byte[32];
        byte[] b = new byte[32];
        dropped.Process(a);
        manual.Process(b);

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(1024)]
    public void Drop_IsChunkBoundaryAgnostic(int dropCount)
    {
        byte[] key = Convert.FromHexString("0102030405060708090a0b0c0d0e0f10");

        Arc4 dropped = new(key);
        dropped.Drop(dropCount);

        Arc4 manual = new(key);
        manual.Process(new byte[dropCount]);

        byte[] a = new byte[8];
        byte[] b = new byte[8];
        dropped.Process(a);
        manual.Process(b);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Process_IsItsOwnInverse()
    {
        byte[] key = Convert.FromHexString("deadbeefcafe");
        byte[] plaintext = "the quick brown fox jumps over the lazy dog"u8.ToArray();

        byte[] buffer = (byte[])plaintext.Clone();
        new Arc4(key).Process(buffer);
        Assert.NotEqual(plaintext, buffer);

        new Arc4(key).Process(buffer);
        Assert.Equal(plaintext, buffer);
    }

    // The world protocol encrypts 4-, 5- and 6-byte headers off one continuous stream. Splitting a
    // call must not change the output, or the client desyncs after the first packet.
    [Fact]
    public void Process_IsContinuousAcrossCalls()
    {
        byte[] key = Convert.FromHexString("0102030405");

        byte[] wholeAtOnce = new byte[15];
        new Arc4(key).Process(wholeAtOnce);

        byte[] piecewise = new byte[15];
        Arc4 cipher = new(key);
        cipher.Process(piecewise.AsSpan(0, 4));
        cipher.Process(piecewise.AsSpan(4, 5));
        cipher.Process(piecewise.AsSpan(9, 6));

        Assert.Equal(wholeAtOnce, piecewise);
    }

    [Fact]
    public void Constructor_RejectsEmptyKey()
    {
        Assert.Throws<ArgumentException>(() => new Arc4([]));
    }

    [Fact]
    public void Drop_RejectsNegativeCount()
    {
        Arc4 cipher = new([1, 2, 3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => cipher.Drop(-1));
    }
}
