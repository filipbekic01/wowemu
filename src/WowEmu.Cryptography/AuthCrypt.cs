using System.Security.Cryptography;

namespace WowEmu.Cryptography;

/// <summary>
/// WotLK 3.3.5a world-packet <b>header</b> encryption.
/// </summary>
/// <remarks>
/// Port of <c>src/common/Cryptography/Authentication/AuthCrypt.cpp</c>.
/// <para>
/// Two independent ARC4-drop1024 streams are keyed with <c>HMAC-SHA1(seed, sessionKey)</c> — note
/// the 16-byte constant is the HMAC <b>key</b> and the 40-byte session key is the <b>message</b>,
/// which is the opposite of the intuitive ordering. Swapping them produces a valid-looking key that
/// decrypts to garbage.
/// </para>
/// <para>
/// Only the 4/5/6 header bytes are ever encrypted; packet bodies are always plaintext. The streams
/// are continuous, so a packet may never be skipped once initialized.
/// </para>
/// </remarks>
public sealed class AuthCrypt
{
    /// <summary>Length of the SRP6-derived session key, in bytes.</summary>
    public const int SessionKeyLength = 40;

    private static ReadOnlySpan<byte> ServerEncryptionSeed =>
    [
        0xCC, 0x98, 0xAE, 0x04, 0xE8, 0x97, 0xEA, 0xCA,
        0x12, 0xDD, 0xC0, 0x93, 0x42, 0x91, 0x53, 0x57
    ];

    private static ReadOnlySpan<byte> ServerDecryptionSeed =>
    [
        0xC2, 0xB3, 0x72, 0x3C, 0xC6, 0xAE, 0xD9, 0xB5,
        0x34, 0x3C, 0x53, 0xEE, 0x2F, 0x43, 0x67, 0xCE
    ];

    private Arc4? _serverEncrypt;
    private Arc4? _clientDecrypt;

    /// <summary>Whether <see cref="Init"/> has run. Encryption starts the instant it does.</summary>
    public bool IsInitialized => _serverEncrypt is not null;

    /// <summary>Derives both keystreams from the session key and drops 1024 bytes of each.</summary>
    public void Init(ReadOnlySpan<byte> sessionKey)
    {
        if (sessionKey.Length != SessionKeyLength)
        {
            throw new ArgumentException(
                $"Session key must be {SessionKeyLength} bytes, got {sessionKey.Length}.",
                nameof(sessionKey));
        }

        Span<byte> encryptKey = stackalloc byte[SHA1.HashSizeInBytes];
        Span<byte> decryptKey = stackalloc byte[SHA1.HashSizeInBytes];
        HMACSHA1.HashData(ServerEncryptionSeed, sessionKey, encryptKey);
        HMACSHA1.HashData(ServerDecryptionSeed, sessionKey, decryptKey);

        _serverEncrypt = new Arc4(encryptKey);
        _clientDecrypt = new Arc4(decryptKey);

        // WoW uses ARC4-drop1024.
        _serverEncrypt.Drop(1024);
        _clientDecrypt.Drop(1024);
    }

    /// <summary>Decrypts a client-to-server packet header in place.</summary>
    public void DecryptRecv(Span<byte> header)
    {
        if (_clientDecrypt is null)
        {
            throw new InvalidOperationException("AuthCrypt.Init has not been called.");
        }

        _clientDecrypt.Process(header);
    }

    /// <summary>Encrypts a server-to-client packet header in place.</summary>
    public void EncryptSend(Span<byte> header)
    {
        if (_serverEncrypt is null)
        {
            throw new InvalidOperationException("AuthCrypt.Init has not been called.");
        }

        _serverEncrypt.Process(header);
    }
}
