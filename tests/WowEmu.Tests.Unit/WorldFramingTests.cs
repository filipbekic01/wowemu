using System.Net;
using System.Net.Sockets;
using WowEmu.Cryptography;
using WowEmu.Network;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The world protocol's packet headers.
/// </summary>
/// <remarks>
/// The size field is big-endian and the opcode little-endian <i>within the same header</i>. Getting
/// that backwards produces sizes in the tens of thousands and opcodes that do not exist, so it
/// looks like a corrupt stream rather than a byte-order mistake — which is why it is worth pinning
/// down directly.
/// </remarks>
public sealed class WorldPacketHeaderTests
{
    [Fact]
    public void ClientHeader_ReadsBigEndianSizeAndLittleEndianOpcode()
    {
        // size = 0x0008 big-endian, opcode = 0x01ED little-endian
        byte[] header = [0x00, 0x08, 0xED, 0x01, 0x00, 0x00];

        Assert.True(WorldPacketHeader.TryReadClient(header, out Opcode opcode, out int payloadLength));
        Assert.Equal(Opcode.CMSG_AUTH_SESSION, opcode);

        // The size field counts the 4-byte opcode, so an 8-byte field means 4 bytes of payload.
        Assert.Equal(4, payloadLength);
    }

    [Fact]
    public void ClientHeader_WithMinimumSize_HasNoPayload()
    {
        byte[] header = [0x00, 0x04, 0x37, 0x00, 0x00, 0x00];

        Assert.True(WorldPacketHeader.TryReadClient(header, out Opcode opcode, out int payloadLength));
        Assert.Equal(Opcode.CMSG_CHAR_ENUM, opcode);
        Assert.Equal(0, payloadLength);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x03, 0x37, 0x00, 0x00, 0x00 })]   // size below the 4-byte floor
    [InlineData(new byte[] { 0xFF, 0xFF, 0x37, 0x00, 0x00, 0x00 })]   // size over upstream's cap
    [InlineData(new byte[] { 0x00, 0x08, 0x37, 0x00, 0x01, 0x00 })]   // opcode wider than uint16
    public void ClientHeader_RejectsOutOfRangeValues(byte[] header)
    {
        Assert.False(WorldPacketHeader.TryReadClient(header, out _, out _));
    }

    [Fact]
    public void ClientHeader_RejectsAShortBuffer()
    {
        Assert.False(WorldPacketHeader.TryReadClient([0x00, 0x08, 0xED], out _, out _));
    }

    /// <summary>The encoded size counts the opcode as well as the payload — off by two shifts everything.</summary>
    [Fact]
    public void ServerHeader_SizeCountsTheOpcode()
    {
        Span<byte> header = stackalloc byte[WorldPacketHeader.ServerSizeLarge];

        int written = WorldPacketHeader.WriteServer(header, Opcode.SMSG_AUTH_CHALLENGE, payloadLength: 40);

        Assert.Equal(4, written);
        Assert.Equal(42, (header[0] << 8) | header[1]);          // big-endian size: 40 + 2
        Assert.Equal(0xEC, header[2]);                           // little-endian opcode
        Assert.Equal(0x01, header[3]);
    }

    [Fact]
    public void ServerHeader_UsesFiveBytes_ForLargePayloads()
    {
        Span<byte> header = stackalloc byte[WorldPacketHeader.ServerSizeLarge];

        int written = WorldPacketHeader.WriteServer(header, Opcode.SMSG_UPDATE_OBJECT, payloadLength: 0x8000);

        Assert.Equal(5, written);

        // The top bit of the first byte flags the three-byte size form.
        Assert.Equal(0x80, header[0] & 0x80);

        int size = ((header[0] & 0x7F) << 16) | (header[1] << 8) | header[2];
        Assert.Equal(0x8002, size);
    }

    [Fact]
    public void ServerHeaderLength_AgreesWithWhatIsWritten()
    {
        Span<byte> header = stackalloc byte[WorldPacketHeader.ServerSizeLarge];

        foreach (int payload in (int[])[0, 1, 100, 0x7FFC, 0x7FFD, 0x8000, 0x10000])
        {
            int written = WorldPacketHeader.WriteServer(header, Opcode.SMSG_PONG, payload);
            Assert.Equal(WorldPacketHeader.ServerHeaderLength(payload), written);
        }
    }

    /// <summary>The boundary between the two header forms, where an off-by-one is easy.</summary>
    [Fact]
    public void ServerHeader_SwitchesFormAtTheThreshold()
    {
        // size = payload + 2, and the large form kicks in above 0x7FFF.
        Assert.Equal(4, WorldPacketHeader.ServerHeaderLength(0x7FFD));
        Assert.Equal(5, WorldPacketHeader.ServerHeaderLength(0x7FFE));
    }
}

/// <summary>
/// End-to-end framing over a real socket pair: split reads, and the continuity of the RC4 header
/// streams once encryption is on.
/// </summary>
/// <remarks>
/// PLAN.md §6 calls out "partial headers across reads must resume correctly" as a Phase 3
/// requirement, and it is not testable from the header codec alone — the bug it guards against is
/// decrypting a header twice, or decrypting one that is only half present, either of which
/// desynchronises the keystream and surfaces much later as a nonsense opcode.
/// </remarks>
public sealed class WorldConnectionTests
{
    private const int SessionKeyLength = 40;

    [Fact]
    public async Task Framing_ResumesAcrossASplitHeader()
    {
        using SocketPair pair = SocketPair.Create();
        using WorldConnection connection = new(pair.Server);

        List<(Opcode Opcode, byte[] Payload)> received = [];
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        Task pump = connection.RunAsync(
            (opcode, payload, _) =>
            {
                received.Add((opcode, payload.ToArray()));
                return Task.FromResult(received.Count < 1);
            },
            cancellation.Token);

        byte[] packet = BuildClientPacket(Opcode.CMSG_PING, [0xDE, 0xAD, 0xBE, 0xEF]);

        // Header split down the middle, then the rest a moment later.
        await pair.Client.SendAsync(packet.AsMemory(0, 3), cancellation.Token);
        await Task.Delay(30, cancellation.Token);
        await pair.Client.SendAsync(packet.AsMemory(3), cancellation.Token);

        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        (Opcode opcode, byte[] payload) = Assert.Single(received);
        Assert.Equal(Opcode.CMSG_PING, opcode);
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], payload);
    }

    [Fact]
    public async Task Framing_SplitsSeveralPacketsFromOneRead()
    {
        using SocketPair pair = SocketPair.Create();
        using WorldConnection connection = new(pair.Server);

        List<Opcode> received = [];
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        Task pump = connection.RunAsync(
            (opcode, _, _) =>
            {
                received.Add(opcode);
                return Task.FromResult(received.Count < 3);
            },
            cancellation.Token);

        byte[] batch =
        [
            .. BuildClientPacket(Opcode.CMSG_PING, [1, 0, 0, 0]),
            .. BuildClientPacket(Opcode.CMSG_CHAR_ENUM, []),
            .. BuildClientPacket(Opcode.CMSG_KEEP_ALIVE, []),
        ];

        await pair.Client.SendAsync(batch, cancellation.Token);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([Opcode.CMSG_PING, Opcode.CMSG_CHAR_ENUM, Opcode.CMSG_KEEP_ALIVE], received);
    }

    /// <summary>
    /// Every header after <c>Init</c> passes through one continuous keystream. If the connection
    /// decrypted a header twice — say by peeking at a partial read — packet two would decode to
    /// garbage even though packet one looked fine.
    /// </summary>
    [Fact]
    public async Task EncryptedHeaders_StayInStepAcrossManyPackets()
    {
        byte[] sessionKey = [.. Enumerable.Range(0, SessionKeyLength).Select(i => (byte)i)];

        using SocketPair pair = SocketPair.Create();
        using WorldConnection connection = new(pair.Server);

        connection.EnableEncryption(sessionKey);

        // The client half of the same handshake. AuthCrypt's names are server-centric: the stream
        // called DecryptRecv is the one the *client* encrypts its outgoing headers with. RC4 is
        // symmetric, so running the header through that same keystream is the encrypt operation.
        AuthCrypt clientSide = new();
        clientSide.Init(sessionKey);

        List<Opcode> received = [];
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        const int PacketCount = 25;

        Task pump = connection.RunAsync(
            (opcode, _, _) =>
            {
                received.Add(opcode);
                return Task.FromResult(received.Count < PacketCount);
            },
            cancellation.Token);

        for (int i = 0; i < PacketCount; i++)
        {
            byte[] packet = BuildClientPacket(Opcode.CMSG_PING, [(byte)i, 0, 0, 0]);

            // Encrypt just the header, in stream order, exactly as a real client does.
            clientSide.DecryptRecv(packet.AsSpan(0, WorldPacketHeader.ClientSize));

            await pair.Client.SendAsync(packet, cancellation.Token);
        }

        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PacketCount, received.Count);
        Assert.All(received, opcode => Assert.Equal(Opcode.CMSG_PING, opcode));
    }

    [Fact]
    public async Task SendAsync_WritesHeaderThenBody()
    {
        using SocketPair pair = SocketPair.Create();
        using WorldConnection connection = new(pair.Server);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        ServerPacket packet = new(Opcode.SMSG_AUTH_CHALLENGE);
        packet.Body.WriteUInt32(1);

        await connection.SendAsync(packet, cancellation.Token);

        byte[] buffer = new byte[8];
        int read = await pair.Client.ReceiveAsync(buffer, cancellation.Token);

        Assert.Equal(8, read);
        Assert.Equal(6, (buffer[0] << 8) | buffer[1]);          // 4 bytes of body + 2 for the opcode
        Assert.Equal(0xEC, buffer[2]);
        Assert.Equal(0x01, buffer[3]);
        Assert.Equal([1, 0, 0, 0], buffer[4..8]);
    }

    /// <summary>A header that cannot be parsed is unrecoverable: there is no framing to resync to.</summary>
    [Fact]
    public async Task MalformedHeader_FailsTheConnection()
    {
        using SocketPair pair = SocketPair.Create();
        using WorldConnection connection = new(pair.Server);

        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));

        Task pump = connection.RunAsync((_, _, _) => Task.FromResult(true), cancellation.Token);

        // Size field of 0 — below the 4-byte floor, so it cannot be a real packet.
        await pair.Client.SendAsync(new byte[] { 0x00, 0x00, 0x37, 0x00, 0x00, 0x00 }, cancellation.Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => pump.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static byte[] BuildClientPacket(Opcode opcode, ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[WorldPacketHeader.ClientSize + payload.Length];

        int size = payload.Length + 4;
        packet[0] = (byte)(size >> 8);
        packet[1] = (byte)size;
        packet[2] = (byte)((ushort)opcode & 0xFF);
        packet[3] = (byte)(((ushort)opcode >> 8) & 0xFF);

        payload.CopyTo(packet.AsSpan(WorldPacketHeader.ClientSize));
        return packet;
    }

    /// <summary>A connected pair of loopback sockets, so framing is tested over a real stream.</summary>
    private sealed class SocketPair : IDisposable
    {
        private SocketPair(Socket client, Socket server)
        {
            Client = client;
            Server = server;
        }

        public Socket Client { get; }

        public Socket Server { get; }

        public static SocketPair Create()
        {
            using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect((IPEndPoint)listener.LocalEndPoint!);

            return new SocketPair(client, listener.Accept());
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }
}
