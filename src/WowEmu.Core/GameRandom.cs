using System.Security.Cryptography;

namespace WowEmu.Core;

/// <summary>
/// The game layer's random numbers: <c>urand</c>, <c>irand</c>, <c>frand</c>, <c>rand_norm</c> and
/// the roll helpers, over a per-thread <see cref="Sfmt19937"/>.
/// </summary>
/// <remarks>
/// Port of <c>src/common/Utilities/Random.{h,cpp}</c>, which layers libstdc++'s distributions on
/// top of SFMT.
/// <para>
/// <b>The draw counts are part of the contract.</b> Upstream's <c>urand</c> is
/// <c>std::uniform_int_distribution</c>, which rejection-samples and therefore consumes a
/// <i>variable</i> number of 32-bit draws; <c>rand_norm</c> is a double distribution that consumes
/// exactly two. Reimplementing these as the "obvious" modulo or single-draw versions would produce
/// a statistically fine but differently-shaped stream, and the two servers would diverge after the
/// first roll — destroying the differential testing the SFMT port exists for. So the libstdc++
/// algorithms are reproduced, not approximated.
/// </para>
/// <para>
/// Each thread gets its own generator, matching upstream's <c>thread_local</c>. Seeds come from the
/// OS unless <see cref="SeedCurrentThread"/> is called — that seed hook is the one thing here the
/// C++ side does not have, and it is what makes a reproducible run possible at all.
/// </para>
/// </remarks>
public static class GameRandom
{
    [ThreadStatic]
    private static Sfmt19937? _generator;

    private static Sfmt19937 Generator => _generator ??= CreateFromEntropy();

    /// <summary>
    /// Reseeds the calling thread's generator, making every subsequent roll on it reproducible.
    /// </summary>
    /// <remarks>
    /// For differential testing against the C++ server, seed both sides with the same value. Note
    /// this is per thread: a run is only reproducible if the work lands on the same threads, which
    /// is exactly why gameplay is pinned to its map's task.
    /// </remarks>
    public static void SeedCurrentThread(uint seed) => _generator = new Sfmt19937(seed);

    /// <inheritdoc cref="SeedCurrentThread(uint)"/>
    public static void SeedCurrentThread(ReadOnlySpan<uint> key) => _generator = new Sfmt19937(key);

    /// <summary>A raw 32-bit draw. <c>rand32()</c>.</summary>
    public static uint Rand32() => Generator.NextUInt32();

    /// <summary>
    /// A uniform integer in <c>[min, max]</c> — <b>inclusive at both ends</b>.
    /// </summary>
    /// <remarks>
    /// The inclusivity is not incidental: the melee attack table rolls <c>urand(0, 10000)</c> and
    /// depends on there being 10001 distinct outcomes. An exclusive upper bound silently shifts
    /// every hit/crit/dodge boundary.
    /// </remarks>
    public static uint Urand(uint min, uint max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);

        return min + UniformUInt32(max - min);
    }

    /// <summary>A uniform signed integer in <c>[min, max]</c>, inclusive.</summary>
    public static int Irand(int min, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);

        // Upstream's distribution works in the unsigned domain and adds the offset back, which is
        // what makes negative ranges come out right without overflowing.
        uint range = unchecked((uint)max - (uint)min);
        return unchecked((int)((uint)min + UniformUInt32(range)));
    }

    /// <summary>A random count of milliseconds between <paramref name="minSeconds"/> and <paramref name="maxSeconds"/>.</summary>
    public static uint Urandms(uint minSeconds, uint maxSeconds) => Urand(minSeconds * 1000, maxSeconds * 1000);

    /// <summary>A uniform float in <c>[min, max)</c>. One draw.</summary>
    public static float Frand(float min, float max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);

        return (GenerateCanonicalSingle() * (max - min)) + min;
    }

    /// <summary>A uniform double in <c>[0, 1)</c>. Two draws.</summary>
    public static double RandNorm() => GenerateCanonicalDouble();

    /// <summary>A uniform double in <c>[0, 100)</c>. Two draws.</summary>
    public static double RandChance() => GenerateCanonicalDouble() * 100.0;

    /// <summary>Whether a percentage roll succeeds. <c>chance</c> is 0-100.</summary>
    public static bool RollChanceF(float chance) => chance > RandChance();

    /// <inheritdoc cref="RollChanceF(float)"/>
    public static bool RollChanceI(int chance) => chance > Irand(0, 99);

    /// <summary>
    /// libstdc++'s <c>uniform_int_distribution</c> over a full-range 32-bit engine, for a range of
    /// <paramref name="range"/> + 1 values.
    /// </summary>
    /// <remarks>
    /// Rejection sampling, not modulo: draws are scaled down by <c>engineRange / bucketCount</c>
    /// and anything landing in the ragged tail is thrown away and redrawn. That rejection is why
    /// the number of draws per call varies, and reproducing it is the whole point.
    /// </remarks>
    private static uint UniformUInt32(uint range)
    {
        const uint EngineRange = uint.MaxValue; // max() - min(), and min() is 0

        Sfmt19937 generator = Generator;

        if (EngineRange > range)
        {
            uint buckets = range + 1;
            uint scaling = EngineRange / buckets;
            uint past = buckets * scaling;

            uint result;
            do
            {
                result = generator.NextUInt32();
            }
            while (result >= past);

            return result / scaling;
        }

        // range == EngineRange: every draw is already in range. (range > EngineRange cannot happen
        // for uint32, so upstream's multi-draw branch has no equivalent here.)
        return generator.NextUInt32();
    }

    /// <summary>
    /// <c>std::generate_canonical&lt;float, 24&gt;</c>: one draw scaled by 2^32.
    /// </summary>
    private static float GenerateCanonicalSingle()
    {
        const float TwoPow32 = 4294967296f;

        float result = Generator.NextUInt32() / TwoPow32;

        // generate_canonical can round up to exactly 1 in the last bit; the standard requires the
        // result stay below it.
        return result >= 1f ? MathF.BitDecrement(1f) : result;
    }

    /// <summary>
    /// <c>std::generate_canonical&lt;double, 53&gt;</c>: two draws, low word first, scaled by 2^64.
    /// </summary>
    private static double GenerateCanonicalDouble()
    {
        const double TwoPow32 = 4294967296.0;
        const double TwoPow64 = 18446744073709551616.0;

        Sfmt19937 generator = Generator;

        double sum = generator.NextUInt32();
        sum += generator.NextUInt32() * TwoPow32;

        double result = sum / TwoPow64;

        return result >= 1.0 ? Math.BitDecrement(1.0) : result;
    }

    private static Sfmt19937 CreateFromEntropy()
    {
        Span<uint> key = stackalloc uint[4];
        RandomNumberGenerator.Fill(System.Runtime.InteropServices.MemoryMarshal.AsBytes(key));
        return new Sfmt19937(key);
    }
}
