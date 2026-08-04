using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using WowEmu.Cryptography;
using WowEmu.Protocol;

namespace WowEmu.Network;

/// <summary>Receives one decoded packet. Return false to close the connection.</summary>
public delegate Task<bool> WorldPacketHandler(Opcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

/// <summary>
/// One world-server client connection: framing, header encryption, and an ordered send path.
/// </summary>
/// <remarks>
/// Port of the transport half of <c>WorldSocket</c>.
/// <para>
/// <b>Only the header is encrypted, and the RC4 streams are stateful and continuous.</b> Each
/// direction has one keystream that runs across the whole session, so every header after
/// <see cref="EnableEncryption"/> must pass through it exactly once, in order. Skipping a packet,
/// encrypting one twice, or decrypting a header while peeking at a partial read desynchronises the
/// stream permanently — and the symptom is a "corrupt" packet several messages later, nowhere near
/// the mistake.
/// </para>
/// <para>
/// That is why a header is decrypted only once it has arrived in full, and why sends are
/// serialized: two packets encrypting their headers concurrently would interleave the keystream.
/// </para>
/// </remarks>
public sealed class WorldConnection(Socket socket) : IDisposable
{
    private readonly Socket _socket = socket;
    private readonly AuthCrypt _crypt = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _headerBuffer = new byte[WorldPacketHeader.ClientSize];

    // Created up front, not in RunAsync: the world protocol opens with the *server* speaking, so
    // SMSG_AUTH_CHALLENGE goes out before the read loop has started.
    private readonly NetworkStream _stream = new(socket, ownsSocket: false);

    // Set once a header has been decrypted and parsed but its payload has not arrived yet. The
    // header must not be touched again while we wait.
    private bool _headerPending;
    private Opcode _pendingOpcode;
    private int _pendingPayloadLength;

    public string RemoteAddress => (_socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";

    /// <summary>Whether header encryption is active.</summary>
    public bool IsEncrypted => _crypt.IsInitialized;

    /// <summary>
    /// Turns on header encryption for both directions.
    /// </summary>
    /// <remarks>
    /// Upstream calls this <b>before</b> verifying the client's digest, deliberately: an
    /// authentication failure still has to be sent as an encrypted packet, because the client has
    /// already switched its own crypt on and cannot read a plaintext header. Verifying first and
    /// initializing after would leave a rejected client staring at a hang instead of an error.
    /// </remarks>
    public void EnableEncryption(ReadOnlySpan<byte> sessionKey) => _crypt.Init(sessionKey);

    /// <summary>Reads packets until the connection closes or a handler asks to stop.</summary>
    public async Task RunAsync(WorldPacketHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        PipeReader reader = PipeReader.Create(_stream);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (TryReadPacket(ref buffer, out Opcode opcode, out ReadOnlySequence<byte> payload))
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent((int)payload.Length);
                    try
                    {
                        payload.CopyTo(rented);

                        if (!await handler(opcode, rented.AsMemory(0, (int)payload.Length), cancellationToken)
                                .ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            // Client vanished; normal.
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits one packet off the front of the buffer, decrypting its header the first time enough
    /// bytes are present.
    /// </summary>
    /// <remarks>
    /// The two-stage <see cref="_headerPending"/> flag is what makes a header that arrives split
    /// across two reads work. Decrypting requires all six bytes, and decryption cannot be repeated,
    /// so the header is consumed and decoded exactly once and the result is held while the payload
    /// catches up.
    /// </remarks>
    private bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out Opcode opcode, out ReadOnlySequence<byte> payload)
    {
        opcode = default;
        payload = default;

        if (!_headerPending)
        {
            if (buffer.Length < WorldPacketHeader.ClientSize)
            {
                return false;
            }

            buffer.Slice(0, WorldPacketHeader.ClientSize).CopyTo(_headerBuffer);
            buffer = buffer.Slice(WorldPacketHeader.ClientSize);

            if (_crypt.IsInitialized)
            {
                _crypt.DecryptRecv(_headerBuffer);
            }

            if (!WorldPacketHeader.TryReadClient(_headerBuffer, out _pendingOpcode, out _pendingPayloadLength))
            {
                // Malformed size or opcode. The stream cannot be resynchronised, so the caller
                // closes; there is no framing to recover to.
                throw new InvalidDataException(
                    $"Malformed packet header from {RemoteAddress}.");
            }

            _headerPending = true;
        }

        if (buffer.Length < _pendingPayloadLength)
        {
            return false;
        }

        opcode = _pendingOpcode;
        payload = buffer.Slice(0, _pendingPayloadLength);
        buffer = buffer.Slice(_pendingPayloadLength);
        _headerPending = false;

        return true;
    }

    /// <summary>Sends one packet, encrypting its header if encryption is on.</summary>
    public async Task SendAsync(ServerPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        int payloadLength = packet.Length;
        int headerLength = WorldPacketHeader.ServerHeaderLength(payloadLength);

        byte[] frame = ArrayPool<byte>.Shared.Rent(headerLength + payloadLength);

        // One packet at a time: the header keystream is stateful, so concurrent sends would
        // interleave it and corrupt every header after the collision.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            WorldPacketHeader.WriteServer(frame.AsSpan(0, headerLength), packet.Opcode, payloadLength);

            if (_crypt.IsInitialized)
            {
                _crypt.EncryptSend(frame.AsSpan(0, headerLength));
            }

            packet.Body.WrittenSpan.CopyTo(frame.AsSpan(headerLength));

            await _stream.WriteAsync(frame.AsMemory(0, headerLength + payloadLength), cancellationToken)
                .ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    public void Dispose()
    {
        _sendLock.Dispose();
        _stream.Dispose();
    }
}
