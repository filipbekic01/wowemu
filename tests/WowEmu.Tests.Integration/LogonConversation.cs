using System.Buffers.Binary;
using System.Numerics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WowEmu.Tests.Integration;

/// <summary>The result of a completed logon: what both sides should now agree on.</summary>
internal sealed record LogonResult(byte[] SessionKey, IReadOnlyList<RealmEntry> Realms);

/// <summary>One realm as the client parses it out of the realm-list packet.</summary>
internal sealed record RealmEntry(string Name, string Address);

/// <summary>
/// Drives the client side of the logon protocol over a socket.
/// </summary>
/// <remarks>
/// A transcription of <c>tools/harness/m1_login.py</c>. Kept as its own type so the tests read as a
/// sequence of protocol steps and assertions rather than as byte arithmetic.
/// </remarks>
internal static class LogonConversation
{
    private const ushort Build = 12340;

    private static ReadOnlySpan<byte> VersionChallenge =>
    [
        0xBA, 0xA3, 0x1E, 0x99, 0xA0, 0x0B, 0x21, 0x57,
        0xFC, 0x37, 0x3F, 0xB3, 0x69, 0xCD, 0xD2, 0xF1
    ];

    /// <summary>
    /// The full logon: challenge, proof, realm list. Throws with the server's code if it refuses.
    /// </summary>
    public static async Task<LogonResult> LogonAsync(Socket socket, string username, string password)
    {
        byte[] user = SrpClient.Normalize(username);

        await socket.SendAsync(ChallengePacket(0x00, user)).ConfigureAwait(false);

        byte[] head = await ReadExactlyAsync(socket, 3, "challenge response").ConfigureAwait(false);

        Assert.Equal(0x00, head[0]);

        if (head[2] != 0x00)
        {
            throw new AuthRefusedException(head[2]);
        }

        byte[] body = await ReadExactlyAsync(socket, 116, "challenge body").ConfigureAwait(false);

        BigInteger serverEphemeral = SrpClient.ToNumber(body.AsSpan(0, 32));

        Assert.Equal(1, body[32]);
        Assert.Equal(7, body[33]);
        Assert.Equal(32, body[34]);
        Assert.Equal(SrpClient.N, SrpClient.ToNumber(body.AsSpan(35, 32)));

        byte[] salt = body[67..99];

        Assert.True(
            body.AsSpan(99, 16).SequenceEqual(VersionChallenge),
            "the 16-byte version challenge constant must be echoed verbatim");

        // x = H(salt, H(USER:PASS)), and the verifier the server stored is g^x mod N.
        byte[] credentials = SrpClient.Normalize($"{username}:{password}");
        BigInteger x = SrpClient.ToNumber(SrpClient.Sha1(salt, SrpClient.Sha1(credentials)));
        BigInteger verifier = BigInteger.ModPow(SrpClient.G, x, SrpClient.N);

        BigInteger privateEphemeral = SrpClient.GeneratePrivateEphemeral();
        BigInteger publicEphemeral = BigInteger.ModPow(SrpClient.G, privateEphemeral, SrpClient.N);

        byte[] publicBytes = SrpClient.ToBytes(publicEphemeral, 32);
        byte[] serverBytes = SrpClient.ToBytes(serverEphemeral, 32);

        BigInteger scrambler = SrpClient.ToNumber(SrpClient.Sha1(publicBytes, serverBytes));

        // S = (B - k*v)^(a + u*x) mod N. The extra + N keeps the base positive when B < k*v.
        BigInteger baseValue =
            (((serverEphemeral - (SrpClient.Multiplier * verifier)) % SrpClient.N) + SrpClient.N) % SrpClient.N;

        BigInteger shared = BigInteger.ModPow(baseValue, privateEphemeral + (scrambler * x), SrpClient.N);
        byte[] sessionKey = SrpClient.Interleave(SrpClient.ToBytes(shared, 32));

        byte[] hashedN = SrpClient.Sha1(SrpClient.ToBytes(SrpClient.N, 32));
        byte[] hashedG = SrpClient.Sha1([7]);
        byte[] ng = new byte[20];

        for (int index = 0; index < 20; index++)
        {
            ng[index] = (byte)(hashedN[index] ^ hashedG[index]);
        }

        byte[] clientProof = SrpClient.Sha1(
            ng, SrpClient.Sha1(user), salt, publicBytes, serverBytes, sessionKey);

        byte[] proofPacket = [0x01, .. publicBytes, .. clientProof, .. new byte[20], 0, 0];
        await socket.SendAsync(proofPacket).ConfigureAwait(false);

        // The success and failure responses are different lengths — 32 bytes against 4 — so the
        // result byte has to be read before it is known how much more is coming. Reading the
        // optimistic 32 unconditionally is a client that hangs forever on a wrong password.
        byte[] proofHead = await ReadExactlyAsync(socket, 2, "logon proof result").ConfigureAwait(false);

        Assert.Equal(0x01, proofHead[0]);

        if (proofHead[1] != 0x00)
        {
            await ReadExactlyAsync(socket, 2, "logon proof refusal padding").ConfigureAwait(false);
            throw new AuthRefusedException(proofHead[1]);
        }

        byte[] proofBody = await ReadExactlyAsync(socket, 30, "logon proof response").ConfigureAwait(false);

        // M2 proves the server derived the same session key. Without this the handshake could
        // "succeed" against a server that agreed to everything and shared no secret.
        byte[] expectedServerProof = SrpClient.Sha1(publicBytes, clientProof, sessionKey);

        Assert.True(
            proofBody.AsSpan(0, 20).SequenceEqual(expectedServerProof),
            "the server's M2 does not match — the two sides derived different session keys");

        return new LogonResult(sessionKey, await ReadRealmListAsync(socket).ConfigureAwait(false));
    }

    /// <summary>
    /// The reconnect handshake, on a connection of its own. It can only succeed if the session key
    /// from an earlier connection was written to the database and read back.
    /// </summary>
    public static async Task ReconnectAsync(Socket socket, string username, byte[] sessionKey)
    {
        byte[] user = SrpClient.Normalize(username);

        await socket.SendAsync(ChallengePacket(0x02, user)).ConfigureAwait(false);

        byte[] head = await ReadExactlyAsync(socket, 2, "reconnect challenge response").ConfigureAwait(false);

        Assert.Equal(0x02, head[0]);

        if (head[1] != 0x00)
        {
            throw new AuthRefusedException(head[1]);
        }

        byte[] body = await ReadExactlyAsync(socket, 32, "reconnect challenge body").ConfigureAwait(false);
        byte[] serverChallenge = body[0..16];

        byte[] clientChallenge = RandomNumberGenerator.GetBytes(16);
        byte[] proof = SrpClient.Sha1(user, clientChallenge, serverChallenge, sessionKey);

        byte[] packet = [0x03, .. clientChallenge, .. proof, .. new byte[20], 0];
        await socket.SendAsync(packet).ConfigureAwait(false);

        byte[] result = await ReadExactlyAsync(socket, 4, "reconnect proof response").ConfigureAwait(false);

        Assert.Equal(0x03, result[0]);

        if (result[1] != 0x00)
        {
            throw new AuthRefusedException(result[1]);
        }
    }

    private static async Task<IReadOnlyList<RealmEntry>> ReadRealmListAsync(Socket socket)
    {
        await socket.SendAsync(new byte[] { 0x10, 0, 0, 0, 0 }).ConfigureAwait(false);

        byte[] header = await ReadExactlyAsync(socket, 3, "realm list header").ConfigureAwait(false);

        Assert.Equal(0x10, header[0]);

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1, 2));
        byte[] payload = await ReadExactlyAsync(socket, length, "realm list body").ConfigureAwait(false);

        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2));
        List<RealmEntry> realms = new(count);

        int cursor = 6;

        for (int index = 0; index < count; index++)
        {
            cursor += 3;                                    // type, locked, flags
            string name = ReadCString(payload, ref cursor);
            string address = ReadCString(payload, ref cursor);
            cursor += 4 + 1 + 1 + 1;                        // population, characters, timezone, id

            realms.Add(new RealmEntry(name, address));
        }

        return realms;
    }

    private static string ReadCString(byte[] payload, ref int cursor)
    {
        int end = Array.IndexOf(payload, (byte)0, cursor);
        Assert.True(end >= 0, "a string in the realm list is not null-terminated");

        string value = Encoding.UTF8.GetString(payload, cursor, end - cursor);
        cursor = end + 1;

        return value;
    }

    /// <summary>
    /// <c>sAuthLogonChallenge_C</c>. The four-character tags go on the wire reversed, which is the
    /// kind of detail that is invisible until a client refuses to talk to you.
    /// </summary>
    private static byte[] ChallengePacket(byte command, byte[] user)
    {
        List<byte> body =
        [
            .. "WoW\0"u8.ToArray().Reverse(),
            3, 3, 5,
            (byte)(Build & 0xFF), (byte)(Build >> 8),
            .. "x86\0"u8.ToArray().Reverse(),
            .. "Win\0"u8.ToArray().Reverse(),
            .. "enUS"u8.ToArray().Reverse(),
            0, 0, 0, 0,                                     // timezone bias
            127, 0, 0, 1,                                   // ip, informational
            (byte)user.Length,
            .. user,
        ];

        return [command, 0x08, (byte)(body.Count & 0xFF), (byte)(body.Count >> 8), .. body];
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, or gives up.
    /// </summary>
    /// <remarks>
    /// The timeout is the important part. A server that never answers is a far more likely failure
    /// than a slow one on loopback, and without a deadline that failure arrives as a test run that
    /// hangs forever — which in CI means a job killed at the six-hour mark with no useful output.
    /// </remarks>
    private static async Task<byte[]> ReadExactlyAsync(Socket socket, int count, string what)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        byte[] buffer = new byte[count];
        int read = 0;

        while (read < count)
        {
            int received;

            try
            {
                received = await socket
                    .ReceiveAsync(buffer.AsMemory(read), timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"the logon server sent nothing for 10s while reading {what} ({read}/{count} bytes)");
            }

            if (received == 0)
            {
                throw new IOException($"connection closed while reading {what} ({read}/{count} bytes)");
            }

            read += received;
        }

        return buffer;
    }
}

/// <summary>The server refused a step of the handshake, with the code it sent.</summary>
internal sealed class AuthRefusedException(byte code)
    : Exception($"the logon server refused the handshake with code 0x{code:X2}")
{
    public byte Code { get; } = code;
}
