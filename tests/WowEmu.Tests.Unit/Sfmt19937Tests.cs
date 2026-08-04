using WowEmu.Core;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Golden vectors emitted by AzerothCore's own <c>deps/SFMT/SFMT.c</c>, compiled and run by
/// <c>tools/vectors/generate_sfmt_vectors.sh</c>.
/// </summary>
/// <remarks>
/// The reference here is the actual C the C++ server runs, not a second transcription. That matters
/// more for SFMT than for the crypto: the whole reason to hand-port this generator is that the bit
/// streams must be identical for differential testing, so the test has to compare against the real
/// thing or it proves nothing.
/// </remarks>
public sealed class Sfmt19937Tests
{
    [Fact]
    public void InitGenRand_MatchesReference_ForSeed42()
    {
        Sfmt19937 sfmt = new(42);

        Assert.Equal(
            [
                1145448892, 1377304885, 2771179739, 1183904139, 1783745685, 712773393, 923673609,
                3049583414, 3404248797, 1650689660, 2437013609, 2597941323, 132947522, 3416976348,
                503063851, 3693594585
            ],
            Draw(sfmt, 16));
    }

    [Fact]
    public void InitGenRand_MatchesReference_ForSeedZero()
    {
        Sfmt19937 sfmt = new(0u);

        Assert.Equal(
            [
                772581976, 265233418, 1048142482, 1602309670, 3373935053, 3413705399, 3210284462,
                863469473, 3245518687, 498391845, 1319171985, 1477607097, 4244505915, 2319058887,
                2197054011, 3168892745
            ],
            Draw(sfmt, 16));
    }

    /// <summary>
    /// <c>init_by_array</c> is the seeding path <c>SFMTRand</c> uses, and the one PLAN.md §6 names
    /// as the Phase 0 exit criterion.
    /// </summary>
    [Fact]
    public void InitByArray_MatchesReference()
    {
        Sfmt19937 sfmt = new([0x1234u, 0x5678u, 0x9ABCu, 0xDEF0u]);

        Assert.Equal(
            [
                2920711183, 3885745737, 3501893680, 856470934, 1421864068, 277361036, 1518638004,
                2328404353, 3355513634, 64329189, 1624587673, 3508467182, 2481792141, 3706480799,
                1925859037, 2913275699
            ],
            Draw(sfmt, 16));
    }

    /// <summary>
    /// Crosses several state regenerations. The first 624 draws come straight out of the seeded
    /// state; everything after that has been through <c>gen_rand_all</c>, which is where the
    /// 128-bit shift logic actually gets exercised.
    /// </summary>
    [Fact]
    public void GenRandAll_MatchesReference_AfterManyDraws()
    {
        Sfmt19937 sfmt = new(42);

        for (int i = 0; i < 10000; i++)
        {
            sfmt.NextUInt32();
        }

        Assert.Equal(
            [
                323725834, 1192195893, 839123441, 2621945755, 2458685380, 2696703724, 1933658456,
                2924511466, 2220086842, 2450663386, 1327376809, 1235759370, 3683148992, 2224285205,
                2181283992, 2827668247
            ],
            Draw(sfmt, 16));
    }

    [Fact]
    public void StateSize_Is624Words()
    {
        Assert.Equal(19937, Sfmt19937.MersenneExponent);
        Assert.Equal(156, Sfmt19937.N);
        Assert.Equal(624, Sfmt19937.N32);
    }

    /// <summary>Two generators seeded alike must agree forever; that is the premise of the port.</summary>
    [Fact]
    public void SameSeed_ProducesSameStream()
    {
        Sfmt19937 first = new(1337);
        Sfmt19937 second = new(1337);

        Assert.Equal(Draw(first, 2000), Draw(second, 2000));
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentStreams()
    {
        Assert.NotEqual(Draw(new Sfmt19937(1), 16), Draw(new Sfmt19937(2), 16));
    }

    private static uint[] Draw(Sfmt19937 sfmt, int count)
    {
        uint[] values = new uint[count];
        sfmt.NextUInt32s(values);
        return values;
    }
}
