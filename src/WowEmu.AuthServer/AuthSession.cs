using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using WowEmu.Core;
using WowEmu.Cryptography;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.AuthServer;

/// <summary>
/// One client connection to the logon server.
/// </summary>
/// <remarks>
/// Port of <c>src/server/apps/authserver/Server/AuthSession.cpp</c>. The protocol is plaintext with
/// no packet header — every packet is a one-byte command followed by a fixed (or, for the challenge,
/// self-describing) payload, so framing is driven entirely by the command table.
/// <para>
/// Handlers take <see cref="ReadOnlyMemory{T}"/> rather than a span because they await the database.
/// Parsing still happens through the span-based <see cref="PacketReader"/>, in sync stretches that
/// never straddle an <c>await</c>.
/// </para>
/// </remarks>
public sealed class AuthSession(
    Socket socket,
    IAccountRepository accounts,
    IBuildRepository builds,
    RealmList realms,
    ILogger logger)
{
    private readonly Socket _socket = socket;
    private readonly IAccountRepository _accounts = accounts;
    private readonly IBuildRepository _builds = builds;
    private readonly RealmList _realms = realms;
    private readonly ILogger _logger = logger;

    private AuthStatus _status = AuthStatus.Challenge;
    private Srp6? _srp6;
    private AuthAccount? _account;
    private ushort _build;
    private string _locale = "enUS";
    private string _os = "Win";
    private byte[]? _reconnectChallenge;

    private string RemoteAddress => (_socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using NetworkStream stream = new(_socket, ownsSocket: false);
        PipeReader reader = PipeReader.Create(stream);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (TryReadPacket(ref buffer, out ReadOnlySequence<byte> packet))
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent((int)packet.Length);
                    try
                    {
                        packet.CopyTo(rented);
                        if (!await DispatchAsync(rented.AsMemory(0, (int)packet.Length), stream, cancellationToken)
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
        catch (IOException)
        {
            // Client vanished mid-read; normal.
        }
        catch (SocketException)
        {
            // Ditto.
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits one complete packet off the front of <paramref name="buffer"/>, if one has arrived.
    /// </summary>
    private bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> packet)
    {
        packet = default;

        if (buffer.Length < 1)
        {
            return false;
        }

        byte command = buffer.FirstSpan.IsEmpty ? buffer.Slice(0, 1).ToArray()[0] : buffer.FirstSpan[0];

        int size = command switch
        {
            (byte)AuthCommand.LogonChallenge or (byte)AuthCommand.ReconnectChallenge
                => AuthProtocol.ChallengeInitialSize,
            (byte)AuthCommand.LogonProof => AuthProtocol.LogonProofSize,
            (byte)AuthCommand.ReconnectProof => AuthProtocol.ReconnectProofSize,
            (byte)AuthCommand.RealmList => AuthProtocol.RealmListRequestSize,
            _ => -1,
        };

        if (size < 0)
        {
            // Upstream discards the whole read buffer on an unknown command and keeps the
            // connection open rather than closing it. Deliberate leniency; preserved here.
            Log.UnknownCommand(_logger, command, RemoteAddress);
            buffer = buffer.Slice(buffer.End);
            return false;
        }

        if (buffer.Length < size)
        {
            return false;
        }

        // A challenge carries its own length. Note the length field counts the bytes *after* the
        // first four, so the packet on the wire is 4 + size.
        if (command is (byte)AuthCommand.LogonChallenge or (byte)AuthCommand.ReconnectChallenge)
        {
            Span<byte> header = stackalloc byte[AuthProtocol.ChallengeInitialSize];
            buffer.Slice(0, AuthProtocol.ChallengeInitialSize).CopyTo(header);
            size += System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);

            if (size > AuthProtocol.MaxChallengeSize)
            {
                Log.OversizedChallenge(_logger, size, RemoteAddress);
                _status = AuthStatus.Closed;
                buffer = buffer.Slice(buffer.End);
                return false;
            }

            if (buffer.Length < size)
            {
                return false;
            }
        }

        packet = buffer.Slice(0, size);
        buffer = buffer.Slice(size);
        return true;
    }

    /// <summary>Runs one packet. Returns false to close the connection.</summary>
    private async Task<bool> DispatchAsync(
        ReadOnlyMemory<byte> packet,
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (_status == AuthStatus.Closed)
        {
            return false;
        }

        AuthCommand command = (AuthCommand)packet.Span[0];

        AuthStatus required = command switch
        {
            AuthCommand.LogonChallenge or AuthCommand.ReconnectChallenge => AuthStatus.Challenge,
            AuthCommand.LogonProof => AuthStatus.LogonProof,
            AuthCommand.ReconnectProof => AuthStatus.ReconnectProof,
            AuthCommand.RealmList => AuthStatus.Authed,
            _ => AuthStatus.Closed,
        };

        if (_status != required)
        {
            Log.CommandOutOfOrder(_logger, command, RemoteAddress, _status);
            return false;
        }

        PacketWriter response = new();

        bool keepOpen = command switch
        {
            AuthCommand.LogonChallenge =>
                await HandleLogonChallengeAsync(packet, response, cancellationToken).ConfigureAwait(false),
            AuthCommand.LogonProof =>
                await HandleLogonProofAsync(packet, response, cancellationToken).ConfigureAwait(false),
            AuthCommand.ReconnectChallenge =>
                await HandleReconnectChallengeAsync(packet, response, cancellationToken).ConfigureAwait(false),
            AuthCommand.ReconnectProof => HandleReconnectProof(packet.Span, response),
            AuthCommand.RealmList => HandleRealmList(response),
            _ => false,
        };

        if (response.Length > 0)
        {
            await stream.WriteAsync(response.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return keepOpen;
    }

    // ------------------------------------------------------------------ logon

    private async Task<bool> HandleLogonChallengeAsync(
        ReadOnlyMemory<byte> packet,
        PacketWriter response,
        CancellationToken cancellationToken)
    {
        _status = AuthStatus.Closed;

        if (!TryParseChallenge(packet.Span, out string login))
        {
            return false;
        }

        response.WriteUInt8((byte)AuthCommand.LogonChallenge);
        response.WriteUInt8(0x00);

        // Build gating is data-driven: a build with no build_info row is rejected outright.
        if (!await _builds.IsSupportedAsync(_build, cancellationToken).ConfigureAwait(false))
        {
            Log.UnsupportedBuild(_logger, _build, RemoteAddress);
            response.WriteUInt8((byte)AuthResult.FailVersionInvalid);
            return true;
        }

        _account = await _accounts.FindAsync(login, cancellationToken).ConfigureAwait(false);
        if (_account is null)
        {
            Log.UnknownAccount(_logger, login, RemoteAddress);
            response.WriteUInt8((byte)AuthResult.FailUnknownAccount);
            return true;
        }

        _srp6 = new Srp6(_account.Username, _account.Salt, _account.Verifier);

        response.WriteUInt8((byte)AuthResult.Success);
        response.WriteBytes(_srp6.B);
        response.WriteUInt8(1);
        response.WriteBytes(Srp6.G);
        response.WriteUInt8(32);
        response.WriteBytes(Srp6.N);
        response.WriteBytes(_srp6.Salt);
        response.WriteBytes(AuthProtocol.VersionChallenge);
        response.WriteUInt8(0x00); // security flags: no PIN, no matrix, no token

        Log.ChallengeSent(_logger, login, _locale, _os, RemoteAddress);

        _status = AuthStatus.LogonProof;
        return true;
    }

    private async Task<bool> HandleLogonProofAsync(
        ReadOnlyMemory<byte> packet,
        PacketWriter response,
        CancellationToken cancellationToken)
    {
        _status = AuthStatus.Closed;

        if (_srp6 is null || _account is null)
        {
            return false;
        }

        // Copied out of the span before the first await: A and M1 are needed after it.
        byte[] a = new byte[32];
        byte[] clientM = new byte[Srp6.DigestLength];

        if (!TryParseProof(packet.Span, a, clientM))
        {
            return false;
        }

        byte[]? sessionKey = _srp6.VerifyChallengeResponse(a, clientM);
        if (sessionKey is null)
        {
            Log.BadPassword(_logger, _account.Username, RemoteAddress);

            // Upstream answers UnknownAccount, never IncorrectPassword, so a wrong password is
            // indistinguishable from a missing account.
            response.WriteUInt8((byte)AuthCommand.LogonProof);
            response.WriteUInt8((byte)AuthResult.FailUnknownAccount);
            response.WriteUInt16(0);
            return true;
        }

        // Persisted, not just remembered: the world server is a separate process and reads this key
        // out of the database to verify CMSG_AUTH_SESSION.
        await _accounts
            .SaveSessionAsync(_account.Id, sessionKey, RemoteAddress, _build, cancellationToken)
            .ConfigureAwait(false);

        _account = _account with { SessionKey = sessionKey };

        byte[] serverProof = Srp6.GetSessionVerifier(a, clientM, sessionKey);

        response.WriteUInt8((byte)AuthCommand.LogonProof);
        response.WriteUInt8(0x00);
        response.WriteBytes(serverProof);
        response.WriteUInt32(_account.Flags);
        response.WriteUInt32(0); // survey id
        response.WriteUInt16(0); // login flags

        Log.Authenticated(_logger, _account.Username, RemoteAddress);

        _status = AuthStatus.Authed;
        return true;
    }

    // ------------------------------------------------------------------ reconnect

    private async Task<bool> HandleReconnectChallengeAsync(
        ReadOnlyMemory<byte> packet,
        PacketWriter response,
        CancellationToken cancellationToken)
    {
        _status = AuthStatus.Closed;

        if (!TryParseChallenge(packet.Span, out string login))
        {
            return false;
        }

        _account = await _accounts.FindAsync(login, cancellationToken).ConfigureAwait(false);
        if (_account?.SessionKey is null)
        {
            // Note the shorter shape here: unlike the logon challenge there is no 0x00 pad byte.
            response.WriteUInt8((byte)AuthCommand.ReconnectChallenge);
            response.WriteUInt8((byte)AuthResult.FailUnknownAccount);
            return true;
        }

        _reconnectChallenge = RandomNumberGenerator.GetBytes(AuthProtocol.ReconnectProofLength);

        response.WriteUInt8((byte)AuthCommand.ReconnectChallenge);
        response.WriteUInt8((byte)AuthResult.Success);
        response.WriteBytes(_reconnectChallenge);
        response.WriteBytes(AuthProtocol.VersionChallenge);

        _status = AuthStatus.ReconnectProof;
        return true;
    }

    private bool HandleReconnectProof(ReadOnlySpan<byte> packet, PacketWriter response)
    {
        _status = AuthStatus.Closed;

        if (_account?.SessionKey is null || _reconnectChallenge is null)
        {
            return false;
        }

        PacketReader reader = new(packet);
        reader.Skip(1); // command
        if (!reader.TryReadBytes(16, out ReadOnlySpan<byte> r1) ||
            !reader.TryReadBytes(20, out ReadOnlySpan<byte> r2))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[20];
        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(_account.Username));
            hash.AppendData(r1);
            hash.AppendData(_reconnectChallenge);
            hash.AppendData(_account.SessionKey);
            hash.GetHashAndReset(expected);
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, r2))
        {
            Log.BadReconnectProof(_logger, _account.Username, RemoteAddress);
            return false;
        }

        response.WriteUInt8((byte)AuthCommand.ReconnectProof);
        response.WriteUInt8((byte)AuthResult.Success);
        response.WriteUInt16(0);

        Log.Reconnected(_logger, _account.Username, RemoteAddress);

        _status = AuthStatus.Authed;
        return true;
    }

    // ------------------------------------------------------------------ realm list

    private bool HandleRealmList(PacketWriter response)
    {
        PacketWriter payload = new();
        int count = 0;

        foreach (Realm realm in _realms.Realms)
        {
            byte flags = (byte)realm.Flags;
            if (realm.Build != _build)
            {
                flags |= (byte)(RealmFlags.Offline | RealmFlags.SpecifyBuild);
            }

            payload.WriteUInt8((byte)realm.Type);
            payload.WriteUInt8((byte)(realm.AllowedSecurityLevel > (_account?.SecurityLevel ?? 0) ? 1 : 0));
            payload.WriteUInt8(flags);
            payload.WriteCString(realm.Name);
            payload.WriteCString(realm.ClientAddress);
            payload.WriteSingle(realm.PopulationLevel);
            payload.WriteUInt8(0); // characters on this realm for this account; Phase 5 fills it in
            payload.WriteUInt8(realm.Timezone);
            payload.WriteUInt8(realm.Id);

            if ((flags & (byte)RealmFlags.SpecifyBuild) != 0)
            {
                payload.WriteUInt8(3);
                payload.WriteUInt8(3);
                payload.WriteUInt8(5);
                payload.WriteUInt16(realm.Build);
            }

            count++;
        }

        payload.WriteUInt8(0x10);
        payload.WriteUInt8(0x00);

        // The size field counts everything after it: the 6-byte count block plus the realm payload.
        const int CountBlockSize = 4 + 2;

        response.WriteUInt8((byte)AuthCommand.RealmList);
        response.WriteUInt16((ushort)(payload.Length + CountBlockSize));
        response.WriteUInt32(0);
        response.WriteUInt16((ushort)count);
        response.WriteBytes(payload.WrittenSpan);

        Log.RealmListSent(_logger, count, RemoteAddress);

        _status = AuthStatus.Authed;
        return true;
    }

    // ------------------------------------------------------------------ shared parsing

    /// <summary>Reads A and M1 out of a logon proof into caller-owned buffers.</summary>
    private static bool TryParseProof(ReadOnlySpan<byte> packet, Span<byte> a, Span<byte> clientM)
    {
        PacketReader reader = new(packet);
        reader.Skip(1); // command

        if (!reader.TryReadBytes(a.Length, out ReadOnlySpan<byte> rawA) ||
            !reader.TryReadBytes(clientM.Length, out ReadOnlySpan<byte> rawM))
        {
            return false;
        }

        rawA.CopyTo(a);
        rawM.CopyTo(clientM);
        return true;
    }

    /// <summary>
    /// Parses the logon/reconnect challenge, which share a layout, and captures build, locale and OS.
    /// </summary>
    private bool TryParseChallenge(ReadOnlySpan<byte> packet, out string login)
    {
        login = string.Empty;

        PacketReader reader = new(packet);
        reader.Skip(1); // command
        reader.Skip(1); // error, unused

        if (!reader.TryReadUInt16(out ushort size))
        {
            return false;
        }

        reader.Skip(4); // gamename, "WoW\0" reversed

        reader.Skip(3); // version1..3
        if (!reader.TryReadUInt16(out _build))
        {
            return false;
        }

        // platform, os and country arrive with their bytes reversed.
        if (!reader.TryReadReversedAscii(4, out _) ||
            !reader.TryReadReversedAscii(4, out string os) ||
            !reader.TryReadReversedAscii(4, out string country))
        {
            return false;
        }

        _os = os;
        _locale = country;

        reader.Skip(4); // timezone bias
        reader.Skip(4); // ip

        if (!reader.TryReadUInt8(out byte loginLength) ||
            !reader.TryReadFixedString(loginLength, out string rawLogin) ||
            !reader.Ok)
        {
            return false;
        }

        // The client's own length field must account for exactly the fixed payload plus the name.
        if (size - AuthProtocol.ChallengeFixedPayloadSize != loginLength)
        {
            Log.MalformedChallenge(_logger, RemoteAddress, size, loginLength);
            return false;
        }

        login = TextTransform.Utf8ToUpperOnlyLatin(rawLogin);
        return true;
    }
}
