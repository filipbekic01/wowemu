using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
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
/// <para>
/// <b>Sending does not touch the socket.</b> <see cref="Send"/> puts a packet on an unbounded
/// channel and returns; one writer task drains it. That is PLAN.md §4.3's replacement for upstream's
/// <c>MPSCQueueIntrusive</c>, and it is what the tick loop needs: gameplay code runs on a map worker
/// and must never block behind a slow client's TCP window, because that worker owns every other
/// object on the map too. The single reader also serializes the keystream by construction, which is
/// a stronger guarantee than the lock it replaces and costs nothing.
/// </para>
/// </remarks>
public sealed class WorldConnection(Socket socket) : IDisposable
{
    private readonly Socket _socket = socket;
    private readonly AuthCrypt _crypt = new();
    private readonly byte[] _headerBuffer = new byte[WorldPacketHeader.ClientSize];

    // Unbounded and single-reader. Unbounded because dropping a packet desynchronises the client
    // far worse than a moment of memory pressure does, and because the writer only falls behind if
    // the client has stopped reading — at which point the connection is about to die anyway.
    private readonly Channel<OutboundItem> _outbound = Channel.CreateUnbounded<OutboundItem>(
        new UnboundedChannelOptions { SingleReader = true });

    // Touched only by the send loop. See EnableEncryption for why this is not just _crypt.IsInitialized.
    private bool _sendEncrypted;

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
    /// <para>
    /// The two directions start at different points, and they have to. Receiving switches on
    /// immediately, because the client's very next packet is already encrypted. Sending switches on
    /// at a <i>position in the send queue</i> rather than at a moment in time: the challenge sent at
    /// the start of the session is plaintext, and if it were still queued when this ran, a flag
    /// checked at write time would encrypt it and the client could not read it. The sentinel below
    /// is what makes "from here on" mean here in the stream. The two ARC4 states are independent, so
    /// starting them at different points is safe.
    /// </para>
    /// </remarks>
    public void EnableEncryption(ReadOnlySpan<byte> sessionKey)
    {
        _crypt.Init(sessionKey);
        _outbound.Writer.TryWrite(OutboundItem.EnableEncryption);
    }

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

    /// <summary>
    /// Queues one packet. Returns immediately; the socket is written by the send loop.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>async</c>. Everything that sends a packet runs on the world tick or a map
    /// worker, and neither may block: a worker waiting on one client's TCP window is a worker not
    /// updating anyone else's map. Ordering is preserved because the channel is FIFO and has a
    /// single reader.
    /// </remarks>
    public void Send(ServerPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Fails only once the channel is completed, which is the connection shutting down. A packet
        // written to a dying connection is not an error worth propagating into gameplay code.
        _outbound.Writer.TryWrite(new OutboundItem(packet));
    }

    /// <summary>
    /// Drains the outbound queue onto the socket until the connection closes.
    /// </summary>
    /// <remarks>
    /// The one place that touches the send keystream. Framing, header encryption and the write all
    /// happen here, on one task, in queue order — which is what keeps the RC4 stream intact without
    /// a lock.
    /// </remarks>
    public async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (OutboundItem item in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Packet is null)
                {
                    // Everything queued before this point went out in plaintext, which is correct.
                    _sendEncrypted = true;
                    continue;
                }

                await WriteAsync(item.Packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // Client vanished. The read loop notices too and closes the session.
        }
    }

    private async Task WriteAsync(ServerPacket packet, CancellationToken cancellationToken)
    {
        int payloadLength = packet.Length;
        int headerLength = WorldPacketHeader.ServerHeaderLength(payloadLength);

        byte[] frame = ArrayPool<byte>.Shared.Rent(headerLength + payloadLength);

        try
        {
            WorldPacketHeader.WriteServer(frame.AsSpan(0, headerLength), packet.Opcode, payloadLength);

            if (_sendEncrypted)
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
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>Stops the send loop once everything already queued has gone out.</summary>
    public void CompleteSending() => _outbound.Writer.TryComplete();

    /// <summary>
    /// One thing to send: a packet, or the point at which header encryption begins.
    /// </summary>
    /// <remarks>
    /// The encryption marker travels in the queue rather than being a flag on the side, so that
    /// "encryption starts now" means the same thing to the writer as it did to the caller. See
    /// <see cref="EnableEncryption"/>.
    /// </remarks>
    private readonly record struct OutboundItem(ServerPacket? Packet)
    {
        public static OutboundItem EnableEncryption => new((ServerPacket?)null);
    }

    public void Dispose()
    {
        CompleteSending();
        _stream.Dispose();
    }
}
