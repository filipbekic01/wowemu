using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using WowEmu.Cryptography;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Golden vectors produced by an independent Python transcription of
/// <c>src/common/Cryptography/Authentication/SRP6.cpp</c>. The generator also implements the
/// <i>client</i> side of SRP6 and asserts both sides derive the same session key, so the vectors are
/// self-validating rather than a snapshot of our own output.
/// </summary>
public sealed class Srp6Tests
{
    private const string NHex = "894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7";

    private const string FixtureSalt = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string FixtureVerifier = "1fa30d5eb9f629733ac9352d1a3797ef3bf7e4c53c9b5a7248ae7c87a7a82d10";
    private const string FixtureB = "01060b10151a1f24292e33383d42474c51565b60656a6f74797e83888d92979c";
    private const string FixtureA = "d941a5a49482ba1c8b87c17578f31d34b96da5c96142b4f2d26fc83306b17e1b";

    // ------------------------------------------------------------------ constants

    [Fact]
    public void N_IsLittleEndianRepresentationOfTheModulus()
    {
        // Upstream reads the hex string back-to-front, then interprets the array as little-endian,
        // so the two reversals cancel and the numeric value equals the hex string as written.
        Assert.Equal(32, Srp6.N.Length);
        Assert.Equal(
            "b79b3e2a87823cab8f5ebfbf8eb10108535006298b5badbd5b53e1895e644b89",
            Convert.ToHexString(Srp6.N).ToLowerInvariant());

        BigInteger asNumber = new(Srp6.N, isUnsigned: true, isBigEndian: false);
        Assert.Equal(BigInteger.Parse("0" + NHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture), asNumber);
    }

    [Fact]
    public void G_IsSeven()
    {
        Assert.Equal([7], Srp6.G.ToArray());
    }

    // ------------------------------------------------------------------ full handshake

    /// <summary>
    /// Walks one complete server-side handshake per vector: verifier derivation, the public
    /// ephemeral key B, and the session key derived from the client's A and proof.
    /// </summary>
    [Theory]
    // username, password, salt, verifier, b, B, A, clientM, sessionKey
    [InlineData(
        "TESTACCOUNT", "TESTPASSWORD",
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f",
        "1fa30d5eb9f629733ac9352d1a3797ef3bf7e4c53c9b5a7248ae7c87a7a82d10",
        "01060b10151a1f24292e33383d42474c51565b60656a6f74797e83888d92979c",
        "22a263c5a46610c346a4c6d55a07867dfd44703b06f37067262a0862db344646",
        "d941a5a49482ba1c8b87c17578f31d34b96da5c96142b4f2d26fc83306b17e1b",
        "f2d3015dda69728dfee704e682467660a60eae43",
        "5ccafde7f2ef9e33a91c32692aae45d9ef0596ba39addc3395a69554ce3a46755c868efceeefd6f9")]
    [InlineData(
        "ADMIN", "SECRET123",
        "030a11181f262d343b424950575e656c737a81888f969da4abb2b9c0c7ced5dc",
        "dec754116e9480398d767da51b33beedf93c9de4d381f2a76782f9d595a0da42",
        "01060b10151a1f24292e33383d42474c51565b60656a6f74797e83888d92979c",
        "a874fab43bbdd76aaf4dde7ed049f96fe4c5926e404b8b4a28539dc347b80155",
        "d941a5a49482ba1c8b87c17578f31d34b96da5c96142b4f2d26fc83306b17e1b",
        "86a9c4a5f4bd05bcad88dca97df51b43e36f65ba",
        "0e3441a4fb5b8b75284dc2dbb69ff3c3efbe316464ca3aabdbaaf8ed1ba45e3bb08733849e181e43")]
    // The important one: this handshake's shared secret S begins with a 0x00 byte, so it only
    // succeeds if SHA1Interleave strips leading zeroes.
    [InlineData(
        "ZEROCASE", "PASSWORD",
        "0b1825323f4c596673808d9aa7b4c1cedbe8f5020f1c293643505d6a7784919e",
        "43b5b19726eab857132610bb9010041d89a3a33902b89cc2bd094f9d2f5da764",
        "01060b10151a1f24292e33383d42474c51565b60656a6f74797e83888d92979c",
        "20a1d21dde3b441ab2fdd6ffa030c9f53ea99f444092dcdcce95bc8fb6891c31",
        "6cd071fbd62604559051e8c1cfb6c45799e134113ef19b5941b5f1274bc97f6c",
        "ebf9db675497045e358a59620852fcaafe565a46",
        "186e6d41b698bd6764064df159298d44cdf16b0cdfdd106b4555bfef4b5cac535a5418f6bfee7318")]
    public void ServerHandshake_MatchesReference(
        string username,
        string password,
        string saltHex,
        string verifierHex,
        string secretBHex,
        string publicBHex,
        string aHex,
        string clientProofHex,
        string sessionKeyHex)
    {
        byte[] salt = Convert.FromHexString(saltHex);

        byte[] verifier = Srp6.CalculateVerifier(username, password, salt);
        Assert.Equal(verifierHex, Convert.ToHexString(verifier).ToLowerInvariant());

        Srp6 srp = new(username, salt, verifier, Convert.FromHexString(secretBHex));
        Assert.Equal(publicBHex, Convert.ToHexString(srp.B).ToLowerInvariant());
        Assert.Equal(saltHex, Convert.ToHexString(srp.Salt).ToLowerInvariant());

        byte[]? sessionKey = srp.VerifyChallengeResponse(
            Convert.FromHexString(aHex), Convert.FromHexString(clientProofHex));

        Assert.NotNull(sessionKey);
        Assert.Equal(Srp6.SessionKeyLength, sessionKey.Length);
        Assert.Equal(sessionKeyHex, Convert.ToHexString(sessionKey).ToLowerInvariant());
    }

    // ------------------------------------------------------------------ registration

    [Fact]
    public void CheckLogin_AcceptsCorrectPasswordAndRejectsWrongOne()
    {
        (byte[] salt, byte[] verifier) = Srp6.MakeRegistrationData("SOMEACCOUNT", "HUNTER2");

        Assert.True(Srp6.CheckLogin("SOMEACCOUNT", "HUNTER2", salt, verifier));
        Assert.False(Srp6.CheckLogin("SOMEACCOUNT", "HUNTER3", salt, verifier));
        Assert.False(Srp6.CheckLogin("OTHERACCOUNT", "HUNTER2", salt, verifier));
    }

    [Fact]
    public void MakeRegistrationData_AlwaysProducesFixedWidthValues()
    {
        // Guards against BigInteger's variable-length ToByteArray leaking through: a verifier that
        // happens to have a zero high byte must still be 32 bytes on the wire.
        for (int i = 0; i < 64; i++)
        {
            (byte[] salt, byte[] verifier) = Srp6.MakeRegistrationData($"ACCOUNT{i}", "PASSWORD");
            Assert.Equal(Srp6.SaltLength, salt.Length);
            Assert.Equal(Srp6.VerifierLength, verifier.Length);
        }
    }

    [Fact]
    public void CalculateVerifier_RejectsWrongSaltLength()
    {
        Assert.Throws<ArgumentException>(() => Srp6.CalculateVerifier("A", "B", new byte[31]));
    }

    // ------------------------------------------------------------------ rejection paths

    [Fact]
    public void VerifyChallengeResponse_RejectsWrongProof()
    {
        Srp6 srp = NewFixture();

        Assert.Null(srp.VerifyChallengeResponse(
            Convert.FromHexString(FixtureA), new byte[Srp6.DigestLength]));
    }

    [Fact]
    public void VerifyChallengeResponse_RejectsAThatIsZero()
    {
        Assert.Null(NewFixture().VerifyChallengeResponse(
            new byte[Srp6.EphemeralKeyLength], new byte[Srp6.DigestLength]));
    }

    [Fact]
    public void VerifyChallengeResponse_RejectsAThatIsCongruentToZeroModN()
    {
        // A == N is not zero as bytes, but is zero mod N. Upstream checks (A % N).IsZero(), not A != 0.
        Assert.Null(NewFixture().VerifyChallengeResponse(Srp6.N, new byte[Srp6.DigestLength]));
    }

    [Fact]
    public void VerifyChallengeResponse_RejectsWrongLengthA()
    {
        Assert.Null(NewFixture().VerifyChallengeResponse(new byte[31], new byte[Srp6.DigestLength]));
    }

    [Fact]
    public void VerifyChallengeResponse_CannotBeUsedTwice()
    {
        Srp6 srp = NewFixture();
        srp.VerifyChallengeResponse(new byte[Srp6.EphemeralKeyLength], new byte[Srp6.DigestLength]);

        Assert.Throws<InvalidOperationException>(
            () => srp.VerifyChallengeResponse(new byte[Srp6.EphemeralKeyLength], new byte[Srp6.DigestLength]));
    }

    [Fact]
    public void Constructor_RejectsWrongLengthInputs()
    {
        byte[] salt = Convert.FromHexString(FixtureSalt);
        byte[] verifier = Convert.FromHexString(FixtureVerifier);

        Assert.Throws<ArgumentException>(() => new Srp6("A", new byte[31], verifier));
        Assert.Throws<ArgumentException>(() => new Srp6("A", salt, new byte[31]));
    }

    [Fact]
    public void Constructor_WithoutExplicitSecret_ProducesDistinctChallenges()
    {
        byte[] salt = Convert.FromHexString(FixtureSalt);
        byte[] verifier = Convert.FromHexString(FixtureVerifier);

        byte[] first = new Srp6("TESTACCOUNT", salt, verifier).B;
        byte[] second = new Srp6("TESTACCOUNT", salt, verifier).B;

        Assert.Equal(Srp6.EphemeralKeyLength, first.Length);
        Assert.NotEqual(first, second);
    }

    // ------------------------------------------------------------------ M2

    [Fact]
    public void GetSessionVerifier_ComputesHashOfAThenProofThenKey()
    {
        byte[] a = Convert.FromHexString(FixtureA);
        byte[] clientM = Convert.FromHexString("f2d3015dda69728dfee704e682467660a60eae43");
        byte[] sessionKey = Convert.FromHexString(
            "5ccafde7f2ef9e33a91c32692aae45d9ef0596ba39addc3395a69554ce3a46755c868efceeefd6f9");

        byte[] expected;
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            hash.AppendData(a);
            hash.AppendData(clientM);
            hash.AppendData(sessionKey);
            expected = hash.GetHashAndReset();
        }

        Assert.Equal(expected, Srp6.GetSessionVerifier(a, clientM, sessionKey));
    }

    // ------------------------------------------------------------------ SHA1Interleave

    [Theory]
    [InlineData("3d24a9ad935f051a1af9fb55251b18ada71291b692c5613deb0d081265fb7b01",
                "146fe2b92795bdfe43a1e0f839df8fee95ef5f74eaba5d48a6b422c0ddda0a51abca0ce6e8c998da")]
    [InlineData("0066c990197a7a2cf999c4a12af9a4964b7547e52e4fc52e0228e00fc12f0f03",
                "803fb6aac64c4e402b0f0eae3fdf81080445c25e6bfddc5ed8f1bb5d391ef40af1d5b3288b25a9bc")]
    [InlineData("0000806d1d4a7e341e979818b2dce648b8df0d5dae705aaa2e188b3fb7118191",
                "874f80bf0b35a335a0079c5f2538b244f9b5ed78026aee9dc35bc01942559f0c85eff1adfaf56681")]
    [InlineData("0000000b24802b51add23346fc68d7d050536c819c1a61fb23ecbf8db7217d85",
                "13e34425e8d967d9d46edf61f79045d71e56b046597980c7b26e98b35ec021389eabe3f586995d1a")]
    [InlineData("0000000000fb966f5b309cbf9a54754276c8607c283ceb2cd55cb3f6d8ce4833",
                "da05ce404ec2a56aa122fc24dabe00a475a05d4cd1df58b700aed637264e33b2072ee776765f60ae")]
    public void Sha1Interleave_MatchesReference(string sHex, string expectedHex)
    {
        byte[] sessionKey = Srp6.Sha1Interleave(Convert.FromHexString(sHex));

        Assert.Equal(expectedHex, Convert.ToHexString(sessionKey).ToLowerInvariant());
    }

    /// <summary>
    /// Guards the trap this whole file exists for.
    /// </summary>
    /// <remarks>
    /// The obvious implementation — split S into even/odd bytes, hash both halves whole — is correct
    /// only when S has no leading zero byte, i.e. about 255 logins out of 256. This asserts the
    /// correct answer actually <b>differs</b> from the naive one, so anyone who "simplifies"
    /// <c>Sha1Interleave</c> gets a red test instead of an intermittent production login failure.
    /// </remarks>
    [Theory]
    [InlineData("0066c990197a7a2cf999c4a12af9a4964b7547e52e4fc52e0228e00fc12f0f03")]
    [InlineData("0000806d1d4a7e341e979818b2dce648b8df0d5dae705aaa2e188b3fb7118191")]
    [InlineData("0000000b24802b51add23346fc68d7d050536c819c1a61fb23ecbf8db7217d85")]
    [InlineData("0000000000fb966f5b309cbf9a54754276c8607c283ceb2cd55cb3f6d8ce4833")]
    public void Sha1Interleave_DiffersFromNaiveImplementation_WhenSHasLeadingZeroes(string sHex)
    {
        byte[] s = Convert.FromHexString(sHex);
        Assert.Equal(0, s[0]); // precondition: this vector must actually start with a zero byte

        Assert.NotEqual(NaiveInterleave(s), Srp6.Sha1Interleave(s));
    }

    [Fact]
    public void Sha1Interleave_AgreesWithNaiveImplementation_WhenSHasNoLeadingZero()
    {
        byte[] s = Convert.FromHexString("3d24a9ad935f051a1af9fb55251b18ada71291b692c5613deb0d081265fb7b01");
        Assert.NotEqual(0, s[0]);

        Assert.Equal(NaiveInterleave(s), Srp6.Sha1Interleave(s));
    }

    [Fact]
    public void Sha1Interleave_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => Srp6.Sha1Interleave(new byte[31]));
    }

    /// <summary>The tempting, wrong implementation: no leading-zero strip.</summary>
    private static byte[] NaiveInterleave(ReadOnlySpan<byte> s)
    {
        byte[] buf0 = new byte[16];
        byte[] buf1 = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            buf0[i] = s[2 * i];
            buf1[i] = s[(2 * i) + 1];
        }

        byte[] hash0 = SHA1.HashData(buf0);
        byte[] hash1 = SHA1.HashData(buf1);

        byte[] result = new byte[40];
        for (int i = 0; i < 20; i++)
        {
            result[2 * i] = hash0[i];
            result[(2 * i) + 1] = hash1[i];
        }

        return result;
    }

    private static Srp6 NewFixture()
    {
        return new Srp6(
            "TESTACCOUNT",
            Convert.FromHexString(FixtureSalt),
            Convert.FromHexString(FixtureVerifier),
            Convert.FromHexString(FixtureB));
    }
}
