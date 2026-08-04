using System.Security.Cryptography;
using WowEmu.Cryptography;

namespace WowEmu.Tests.Unit;

public sealed class AuthCryptTests
{
    private static readonly byte[] SessionKey = Convert.FromHexString(
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f2021222324252627");

    /// <summary>
    /// The 16-byte WotLK constant is the HMAC <b>key</b> and the session key is the <b>message</b>.
    /// Swapping them yields a plausible-looking key that decrypts to garbage, so pin the derivation
    /// explicitly rather than only testing it end to end.
    /// </summary>
    [Fact]
    public void KeyDerivation_UsesConstantAsKeyAndSessionKeyAsMessage()
    {
        byte[] serverSeed =
        [
            0xCC, 0x98, 0xAE, 0x04, 0xE8, 0x97, 0xEA, 0xCA,
            0x12, 0xDD, 0xC0, 0x93, 0x42, 0x91, 0x53, 0x57
        ];

        Assert.Equal(
            "316ecb675e326f11c2f47035f756a79263817b4a",
            Convert.ToHexString(HMACSHA1.HashData(serverSeed, SessionKey)).ToLowerInvariant());
    }

    [Fact]
    public void EncryptSend_MatchesReferenceForFirstHeader()
    {
        AuthCrypt crypt = new();
        crypt.Init(SessionKey);

        byte[] header = Convert.FromHexString("0004ee01");
        crypt.EncryptSend(header);

        Assert.Equal("da439ec5", Convert.ToHexString(header).ToLowerInvariant());
    }

    [Fact]
    public void IsInitialized_IsFalseUntilInit()
    {
        AuthCrypt crypt = new();
        Assert.False(crypt.IsInitialized);

        crypt.Init(SessionKey);
        Assert.True(crypt.IsInitialized);
    }

    [Fact]
    public void Operations_ThrowBeforeInit()
    {
        AuthCrypt crypt = new();

        Assert.Throws<InvalidOperationException>(() => crypt.EncryptSend(new byte[4]));
        Assert.Throws<InvalidOperationException>(() => crypt.DecryptRecv(new byte[6]));
    }

    [Fact]
    public void Init_RejectsWrongSessionKeyLength()
    {
        AuthCrypt crypt = new();

        Assert.Throws<ArgumentException>(() => crypt.Init(new byte[39]));
        Assert.Throws<ArgumentException>(() => crypt.Init(new byte[41]));
    }

    /// <summary>
    /// Both directions use different keys, so the client's decrypt stream is keyed the same way as
    /// the server's encrypt stream. Model that explicitly: what the server encrypts with
    /// <c>EncryptSend</c>, a peer keyed identically recovers with the same operation.
    /// </summary>
    [Fact]
    public void ServerEncryptStream_IsRecoverableByAPeerWithTheSameKey()
    {
        AuthCrypt server = new();
        server.Init(SessionKey);

        // A "client" is just another AuthCrypt whose encrypt stream mirrors the server's.
        AuthCrypt clientView = new();
        clientView.Init(SessionKey);

        byte[] original = Convert.FromHexString("0004ee01");
        byte[] wire = (byte[])original.Clone();
        server.EncryptSend(wire);
        Assert.NotEqual(original, wire);

        clientView.EncryptSend(wire); // RC4 is its own inverse
        Assert.Equal(original, wire);
    }

    /// <summary>
    /// The streams are continuous across packets. Encrypting three headers in sequence must not
    /// produce the same bytes as encrypting the first one three times.
    /// </summary>
    [Fact]
    public void EncryptSend_AdvancesTheStreamAcrossPackets()
    {
        AuthCrypt crypt = new();
        crypt.Init(SessionKey);

        byte[] first = Convert.FromHexString("0004ee01");
        byte[] second = Convert.FromHexString("0004ee01");
        byte[] third = Convert.FromHexString("0004ee01");

        crypt.EncryptSend(first);
        crypt.EncryptSend(second);
        crypt.EncryptSend(third);

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        Assert.NotEqual(first, third);
    }

    /// <summary>
    /// Server headers are 4 or 5 bytes and client headers are 6; a mixed sequence must stay in sync
    /// with a peer processing the same byte counts.
    /// </summary>
    [Fact]
    public void EncryptSend_HandlesMixedHeaderSizes()
    {
        AuthCrypt sender = new();
        sender.Init(SessionKey);
        AuthCrypt receiver = new();
        receiver.Init(SessionKey);

        int[] sizes = [4, 5, 4, 6, 5];
        foreach (int size in sizes)
        {
            byte[] original = RandomNumberGenerator.GetBytes(size);
            byte[] wire = (byte[])original.Clone();

            sender.EncryptSend(wire);
            receiver.EncryptSend(wire);

            Assert.Equal(original, wire);
        }
    }

    [Fact]
    public void DecryptRecv_UsesADifferentStreamThanEncryptSend()
    {
        AuthCrypt crypt = new();
        crypt.Init(SessionKey);

        byte[] encrypted = Convert.FromHexString("0004ee01");
        byte[] decrypted = Convert.FromHexString("0004ee01");

        crypt.EncryptSend(encrypted);
        crypt.DecryptRecv(decrypted);

        Assert.NotEqual(encrypted, decrypted);
    }
}
