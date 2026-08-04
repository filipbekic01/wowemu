namespace WowEmu.Core;

/// <summary>
/// SIMD-oriented Fast Mersenne Twister, MEXP 19937 — the generator behind every random roll in the
/// game layer.
/// </summary>
/// <remarks>
/// Hand-port of <c>deps/SFMT/SFMT.c</c> with <c>SFMT-params19937.h</c>, scalar path.
/// <para>
/// <b>Why port it at all, when .NET has a perfectly good RNG?</b> Not for replay — upstream seeds
/// from <c>std::random_device</c> and is already non-reproducible run to run. It is for
/// <b>differential testing</b>: seed this and the C++ server identically, drive both with the same
/// trace, and loot rolls, melee outcomes and <c>LootGroup::Roll</c> results can be diffed directly
/// instead of argued about statistically. That only works if the bit stream matches exactly, which
/// is why this is a transcription rather than an equivalent.
/// </para>
/// <para>
/// The 128-bit words are kept as a flat <c>uint[624]</c> in the same order the C union exposes
/// them, so <see cref="NextUInt32"/> walks the array exactly as <c>psfmt32[idx++]</c> does.
/// </para>
/// </remarks>
public sealed class Sfmt19937
{
    /// <summary>Mersenne exponent. The period is 2^19937 - 1.</summary>
    public const int MersenneExponent = 19937;

    /// <summary>Number of 128-bit words of state.</summary>
    public const int N = (MersenneExponent / 128) + 1;

    /// <summary>Number of 32-bit words of state.</summary>
    public const int N32 = N * 4;

    private const int Pos1 = 122;
    private const int Sl1 = 18;
    private const int Sl2 = 1;
    private const int Sr1 = 11;
    private const int Sr2 = 1;

    private const uint Msk1 = 0xDFFFFFEFu;
    private const uint Msk2 = 0xDDFECB7Fu;
    private const uint Msk3 = 0xBFFAFFFFu;
    private const uint Msk4 = 0xBFFFFFF6u;

    private const uint Parity1 = 0x00000001u;
    private const uint Parity2 = 0x00000000u;
    private const uint Parity3 = 0x00000000u;
    private const uint Parity4 = 0x13C9E684u;

    private readonly uint[] _state = new uint[N32];
    private int _index;

    /// <summary>Seeds with a single 32-bit value (<c>sfmt_init_gen_rand</c>).</summary>
    public Sfmt19937(uint seed) => InitGenRand(seed);

    /// <summary>Seeds from an array (<c>sfmt_init_by_array</c>).</summary>
    public Sfmt19937(ReadOnlySpan<uint> key) => InitByArray(key);

    /// <summary>The next 32 random bits.</summary>
    public uint NextUInt32()
    {
        if (_index >= N32)
        {
            GenRandAll();
            _index = 0;
        }

        return _state[_index++];
    }

    /// <summary>Fills a buffer, one draw per element.</summary>
    public void NextUInt32s(Span<uint> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = NextUInt32();
        }
    }

    private void InitGenRand(uint seed)
    {
        _state[0] = seed;

        for (int i = 1; i < N32; i++)
        {
            _state[i] = (1812433253u * (_state[i - 1] ^ (_state[i - 1] >> 30))) + (uint)i;
        }

        _index = N32;
        PeriodCertification();
    }

    private void InitByArray(ReadOnlySpan<uint> key)
    {
        const int Size = N * 4;
        int lag = Size >= 623 ? 11 : Size >= 68 ? 7 : Size >= 39 ? 5 : 3;
        int mid = (Size - lag) / 2;

        // memset(sfmt, 0x8b, sizeof(sfmt_t)) — every state byte starts at 0x8b, not zero.
        _state.AsSpan().Fill(0x8B8B8B8Bu);

        int count = key.Length + 1 > N32 ? key.Length + 1 : N32;

        uint r = Func1(_state[0] ^ _state[mid] ^ _state[N32 - 1]);
        _state[mid] += r;
        r += (uint)key.Length;
        _state[mid + lag] += r;
        _state[0] = r;

        count--;

        int i = 1;
        int j = 0;

        for (; j < count && j < key.Length; j++)
        {
            r = Func1(_state[i] ^ _state[(i + mid) % N32] ^ _state[(i + N32 - 1) % N32]);
            _state[(i + mid) % N32] += r;
            r += key[j] + (uint)i;
            _state[(i + mid + lag) % N32] += r;
            _state[i] = r;
            i = (i + 1) % N32;
        }

        for (; j < count; j++)
        {
            r = Func1(_state[i] ^ _state[(i + mid) % N32] ^ _state[(i + N32 - 1) % N32]);
            _state[(i + mid) % N32] += r;
            r += (uint)i;
            _state[(i + mid + lag) % N32] += r;
            _state[i] = r;
            i = (i + 1) % N32;
        }

        for (j = 0; j < N32; j++)
        {
            r = Func2(_state[i] + _state[(i + mid) % N32] + _state[(i + N32 - 1) % N32]);
            _state[(i + mid) % N32] ^= r;
            r -= (uint)i;
            _state[(i + mid + lag) % N32] ^= r;
            _state[i] = r;
            i = (i + 1) % N32;
        }

        _index = N32;
        PeriodCertification();
    }

    private static uint Func1(uint x) => (x ^ (x >> 27)) * 1664525u;

    private static uint Func2(uint x) => (x ^ (x >> 27)) * 1566083941u;

    /// <summary>
    /// Guarantees the period by fixing up the state's parity.
    /// </summary>
    /// <remarks>
    /// Skipping this looks harmless — the generator still produces numbers — but the sequence can
    /// land in a short cycle. It also changes the stream, which breaks the differential-testing
    /// premise outright.
    /// </remarks>
    private void PeriodCertification()
    {
        ReadOnlySpan<uint> parity = [Parity1, Parity2, Parity3, Parity4];

        uint inner = 0;
        for (int i = 0; i < 4; i++)
        {
            inner ^= _state[i] & parity[i];
        }

        for (int i = 16; i > 0; i >>= 1)
        {
            inner ^= inner >> i;
        }

        if ((inner & 1) == 1)
        {
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            uint work = 1;
            for (int j = 0; j < 32; j++)
            {
                if ((work & parity[i]) != 0)
                {
                    _state[i] ^= work;
                    return;
                }

                work <<= 1;
            }
        }
    }

    /// <summary>Regenerates the whole state array in place.</summary>
    private void GenRandAll()
    {
        int r1 = N - 2;
        int r2 = N - 1;

        int i = 0;
        for (; i < N - Pos1; i++)
        {
            DoRecursion(i, i, i + Pos1, r1, r2);
            r1 = r2;
            r2 = i;
        }

        for (; i < N; i++)
        {
            DoRecursion(i, i, i + Pos1 - N, r1, r2);
            r1 = r2;
            r2 = i;
        }
    }

    /// <summary>
    /// The core recursion over four 128-bit words. Indices are word128 indices, not array offsets.
    /// </summary>
    /// <remarks>
    /// <paramref name="rWord"/> and <paramref name="aWord"/> are the same word in normal operation,
    /// so every input is read into locals before anything is written back.
    /// </remarks>
    private void DoRecursion(int rWord, int aWord, int bWord, int cWord, int dWord)
    {
        int a = aWord * 4;
        int b = bWord * 4;
        int c = cWord * 4;
        int d = dWord * 4;
        int r = rWord * 4;

        uint a0 = _state[a], a1 = _state[a + 1], a2 = _state[a + 2], a3 = _state[a + 3];
        uint b0 = _state[b], b1 = _state[b + 1], b2 = _state[b + 2], b3 = _state[b + 3];
        uint c0 = _state[c], c1 = _state[c + 1], c2 = _state[c + 2], c3 = _state[c + 3];
        uint d0 = _state[d], d1 = _state[d + 1], d2 = _state[d + 2], d3 = _state[d + 3];

        // lshift128(x, a, SL2) — a 128-bit shift by whole bytes, done as two 64-bit halves.
        ulong aLow = ((ulong)a1 << 32) | a0;
        ulong aHigh = ((ulong)a3 << 32) | a2;
        ulong xLow = aLow << (Sl2 * 8);
        ulong xHigh = (aHigh << (Sl2 * 8)) | (aLow >> (64 - (Sl2 * 8)));

        // rshift128(y, c, SR2)
        ulong cLow = ((ulong)c1 << 32) | c0;
        ulong cHigh = ((ulong)c3 << 32) | c2;
        ulong yLow = (cLow >> (Sr2 * 8)) | (cHigh << (64 - (Sr2 * 8)));
        ulong yHigh = cHigh >> (Sr2 * 8);

        _state[r] = a0 ^ (uint)xLow ^ ((b0 >> Sr1) & Msk1) ^ (uint)yLow ^ (d0 << Sl1);
        _state[r + 1] = a1 ^ (uint)(xLow >> 32) ^ ((b1 >> Sr1) & Msk2) ^ (uint)(yLow >> 32) ^ (d1 << Sl1);
        _state[r + 2] = a2 ^ (uint)xHigh ^ ((b2 >> Sr1) & Msk3) ^ (uint)yHigh ^ (d2 << Sl1);
        _state[r + 3] = a3 ^ (uint)(xHigh >> 32) ^ ((b3 >> Sr1) & Msk4) ^ (uint)(yHigh >> 32) ^ (d3 << Sl1);
    }
}
