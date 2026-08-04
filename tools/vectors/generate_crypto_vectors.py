"""Independent reference implementation of AzerothCore's SRP6 + ARC4, transcribed
directly from:
  src/common/Cryptography/Authentication/SRP6.cpp
  src/common/Cryptography/Authentication/AuthCrypt.cpp
  src/common/Cryptography/BigNumber.cpp   (LE default both directions)
  src/common/Utilities/Util.cpp           (HexStrToByteArray reverse semantics)

Used only to generate golden vectors for the C# unit tests.
"""
import hashlib, hmac, json, os, random

# ---------------------------------------------------------------- constants
N_HEX = "894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7"


def hexstr_to_bytes(s, reverse=False):
    """Mirrors Acore::Impl::HexStrToByteArray (Util.cpp:569)."""
    pairs = [s[i:i + 2] for i in range(0, len(s), 2)]
    if reverse:
        pairs = pairs[::-1]
    return bytes(int(p, 16) for p in pairs)


N_BYTES = hexstr_to_bytes(N_HEX, reverse=True)   # little-endian, 32 bytes
G_BYTES = bytes([7])


def le(b):
    """bytes(LE) -> int   == BigNumber(array, littleEndian=true)"""
    return int.from_bytes(b, "little")


def le_pad(n, size):
    """int -> bytes(LE, zero-padded)  == BN_bn2lebinpad / ToByteArray<size>()"""
    return n.to_bytes(size, "little")


N = le(N_BYTES)
G = le(G_BYTES)
assert N == int(N_HEX, 16), "double reversal must cancel"

sha1 = lambda *parts: hashlib.sha1(b"".join(parts)).digest()


# ---------------------------------------------------------------- SRP6
def calculate_verifier(username, password, salt):
    """v = g ^ H(s || H(u || ':' || p)) mod N   -> 32 bytes LE"""
    inner = sha1(username.encode(), b":", password.encode())
    x = sha1(salt, inner)
    return le_pad(pow(G, le(x), N), 32)


def compute_B(b_bytes, verifier):
    """B = (g^b + 3v) mod N   -> 32 bytes LE"""
    b = le(b_bytes)
    v = le(verifier)
    return le_pad((pow(G, b, N) + v * 3) % N, 32)


def sha1_interleave(S):
    """SRP6.cpp SHA1Interleave -- note the leading-zero strip + odd correction."""
    buf0 = bytes(S[2 * i + 0] for i in range(16))
    buf1 = bytes(S[2 * i + 1] for i in range(16))

    p = 0
    while p < 32 and S[p] == 0:
        p += 1
    if p & 1:
        p += 1
    p //= 2

    hash0 = sha1(buf0[p:])
    hash1 = sha1(buf1[p:])

    K = bytearray(40)
    for i in range(20):
        K[2 * i + 0] = hash0[i]
        K[2 * i + 1] = hash1[i]
    return bytes(K)


def server_verify(username, salt, verifier, b_bytes, A_bytes, client_M):
    """SRP6::VerifyChallengeResponse -> session key or None."""
    B = compute_B(b_bytes, verifier)
    A = le(A_bytes)
    if A % N == 0:
        return None, B

    u = le(sha1(A_bytes, B))
    v = le(verifier)
    b = le(b_bytes)
    S = le_pad(pow(A * pow(v, u, N), b, N), 32)
    K = sha1_interleave(S)

    NgHash = bytes(x ^ y for x, y in zip(sha1(N_BYTES), sha1(G_BYTES)))
    I = sha1(username.encode())
    ourM = sha1(NgHash, I, salt, A_bytes, B, K)
    return (K if ourM == client_M else None), B


def client_proof(username, password, salt, a_bytes, A_bytes, B_bytes):
    """Client side of SRP6, so we can produce a *valid* M1 for the test."""
    inner = sha1(username.encode(), b":", password.encode())
    x = le(sha1(salt, inner))
    u = le(sha1(A_bytes, B_bytes))
    a = le(a_bytes)
    B = le(B_bytes)

    S = le_pad(pow((B - 3 * pow(G, x, N)) % N, a + u * x, N), 32)
    K = sha1_interleave(S)

    NgHash = bytes(p ^ q for p, q in zip(sha1(N_BYTES), sha1(G_BYTES)))
    I = sha1(username.encode())
    M = sha1(NgHash, I, salt, A_bytes, B_bytes, K)
    return M, K, S


# ---------------------------------------------------------------- ARC4
def rc4_keystream(key, nbytes):
    S = list(range(256))
    j = 0
    for i in range(256):
        j = (j + S[i] + key[i % len(key)]) & 0xFF
        S[i], S[j] = S[j], S[i]
    out = bytearray()
    i = j = 0
    for _ in range(nbytes):
        i = (i + 1) & 0xFF
        j = (j + S[i]) & 0xFF
        S[i], S[j] = S[j], S[i]
        out.append(S[(S[i] + S[j]) & 0xFF])
    return bytes(out)


def rc4_process(key, data, drop=0):
    ks = rc4_keystream(key, drop + len(data))[drop:]
    return bytes(a ^ b for a, b in zip(data, ks))


# AuthCrypt.cpp:7-17
SERVER_ENCRYPT_SEED = bytes([0xCC, 0x98, 0xAE, 0x04, 0xE8, 0x97, 0xEA, 0xCA,
                             0x12, 0xDD, 0xC0, 0x93, 0x42, 0x91, 0x53, 0x57])
SERVER_DECRYPT_SEED = bytes([0xC2, 0xB3, 0x72, 0x3C, 0xC6, 0xAE, 0xD9, 0xB5,
                             0x34, 0x3C, 0x53, 0xEE, 0x2F, 0x43, 0x67, 0xCE])


# ---------------------------------------------------------------- generation
def find_a_with(username, password, salt, verifier, b_bytes, want_leading_zero):
    """Search a client secret 'a' whose resulting S does/doesn't start with 0x00."""
    B = compute_B(b_bytes, verifier)
    # Start from a large, realistic secret rather than a=1 (which makes A == g == 7
    # and would not exercise the modular arithmetic at all).
    base = 0x5F3A9C21D4E80B7766A1C359E2074FD8B6C09E1A3D582746F0B9CA13E85D6072
    for seed in range(400000):
        a_bytes = ((base + seed) % N).to_bytes(32, "little")
        A_bytes = le_pad(pow(G, le(a_bytes), N), 32)
        if le(A_bytes) % N == 0:
            continue
        M, K, S = client_proof(username, password, salt, a_bytes, A_bytes, B_bytes=B)
        if (S[0] == 0) == want_leading_zero:
            return a_bytes, A_bytes, M, K, S
    raise RuntimeError("not found")


def main():
    out = {}

    # --- RC4: RFC 6229 self-check, so the vectors we bake in are trustworthy
    rfc = {
        "0102030405": "b2396305f03dc027ccc3524a0a1118a8",
        "0102030405060708090a0b0c0d0e0f10": "9ac7cc9a609d1ef7b2932899cde41b97",
    }
    for k, expect in rfc.items():
        got = rc4_keystream(bytes.fromhex(k), 16).hex()
        assert got == expect, f"RC4 self-check FAILED for {k}: {got} != {expect}"
    print("RC4 matches RFC 6229 vectors")

    out["rc4"] = [
        {"key": k, "offset": 0, "keystream": v} for k, v in rfc.items()
    ]
    out["rc4_drop1024"] = {
        "key": "0102030405060708090a0b0c0d0e0f10",
        "plaintext": "00" * 16,
        "ciphertext": rc4_process(bytes.fromhex("0102030405060708090a0b0c0d0e0f10"),
                                  bytes(16), drop=1024).hex(),
    }

    # --- AuthCrypt key derivation
    session_key = bytes(range(40))
    out["authcrypt"] = {
        "sessionKey": session_key.hex(),
        "serverEncryptKey": hmac.new(SERVER_ENCRYPT_SEED, session_key, hashlib.sha1).hexdigest(),
        "clientDecryptKey": hmac.new(SERVER_DECRYPT_SEED, session_key, hashlib.sha1).hexdigest(),
        # header of SMSG_AUTH_RESPONSE-ish size, encrypted as the very first thing
        "plainHeader": "0004ee01",
        "encryptedHeader": rc4_process(
            hmac.new(SERVER_ENCRYPT_SEED, session_key, hashlib.sha1).digest(),
            bytes.fromhex("0004ee01"), drop=1024).hex(),
    }

    # --- constants
    out["constants"] = {"N_le": N_BYTES.hex(), "N_int_hex": format(N, "x"), "g": 7}

    # --- SRP6 cases
    cases = []
    fixtures = [
        ("TESTACCOUNT", "TESTPASSWORD", bytes(range(32)), False),
        ("ADMIN", "SECRET123", bytes([(i * 7 + 3) & 0xFF for i in range(32)]), False),
        # the important one: force S to have a leading zero byte
        ("ZEROCASE", "PASSWORD", bytes([(i * 13 + 11) & 0xFF for i in range(32)]), True),
    ]
    for username, password, salt, want_zero in fixtures:
        verifier = calculate_verifier(username, password, salt)
        b_bytes = bytes([(i * 5 + 1) & 0xFF for i in range(32)])
        a_bytes, A_bytes, M, K, S = find_a_with(
            username, password, salt, verifier, b_bytes, want_zero)
        K_srv, B = server_verify(username, salt, verifier, b_bytes, A_bytes, M)
        assert K_srv == K, "server/client session keys disagree"
        cases.append({
            "username": username, "password": password,
            "salt": salt.hex(), "verifier": verifier.hex(),
            "b": b_bytes.hex(), "B": B.hex(),
            "A": A_bytes.hex(), "S": S.hex(),
            "clientM": M.hex(), "sessionKey": K.hex(),
            "sHasLeadingZero": S[0] == 0,
        })
        print(f"{username}: S[0]=0x{S[0]:02x} leadingZero={S[0] == 0}")

    out["srp6"] = cases

    # --- SHA1Interleave direct vectors, incl. multi-zero prefixes
    inter = []
    rng = random.Random(20260804)  # fixed seed: this script must be reproducible
    for prefix_zeros in (0, 1, 2, 3, 5):
        S = bytearray(rng.getrandbits(8) for _ in range(32))
        for i in range(prefix_zeros):
            S[i] = 0
        if prefix_zeros < 32:
            S[prefix_zeros] = S[prefix_zeros] or 0x5A
        inter.append({"S": bytes(S).hex(), "K": sha1_interleave(bytes(S)).hex(),
                      "zeros": prefix_zeros})
    out["interleave"] = inter

    # --- SessionKeyGenerator (SessionKeyGenerator.h), as used by Warden
    def session_key_generator(seed, nbytes):
        half = len(seed) // 2
        o1 = sha1(seed[:half])
        o2 = sha1(seed[half:])
        o0 = sha1(o1, bytes(20), o2)   # o0 starts as 20 zero bytes
        out = bytearray()
        pos = 0
        for _ in range(nbytes):
            if pos == 20:
                o0 = sha1(o1, o0, o2)
                pos = 0
            out.append(o0[pos])
            pos += 1
        return bytes(out)

    skg_seed = bytes(range(40))
    out["sessionKeyGenerator"] = {
        "seed": skg_seed.hex(),
        # Warden takes 16 bytes C->S then 16 bytes S->C; 64 crosses three refills.
        "output64": session_key_generator(skg_seed, 64).hex(),
    }

    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "vectors.json")
    with open(path, "w") as f:
        json.dump(out, f, indent=1)
    print("wrote", path)


if __name__ == "__main__":
    main()
