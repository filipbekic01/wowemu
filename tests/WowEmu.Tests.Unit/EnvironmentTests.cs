using WowEmu.Data.Client;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Drowning, fatigue, lava and long falls.
/// </summary>
/// <remarks>
/// The damage the world deals on its own. What is worth pinning is the shape of the timers rather
/// than the numbers: a bar that drains, refills ten times faster than it drained, and bills once a
/// second while empty — and, above all, that a disabled bar and an empty one are different things.
/// </remarks>
public sealed class EnvironmentTests
{
    private const uint MaxHealth = 1000;
    private const uint Level = 10;

    /// <summary>A fixed roll, so the damage is exactly the fifth-of-health term.</summary>
    private static uint NoScatter(uint min, uint max) => min;

    [Fact]
    public void OutOfWater_NothingHappens()
    {
        PlayerEnvironment environment = new();

        environment.Refresh(LiquidData.None, isAlive: true);
        EnvironmentUpdate update = environment.Update(1000, MaxHealth, Level, true, NoScatter);

        Assert.Empty(update.Timers);
        Assert.Empty(update.Hits);
        Assert.True(environment.IsIdle);
    }

    /// <summary>
    /// Going under starts the breath bar full, and says so once.
    /// </summary>
    /// <remarks>
    /// The scale is negative because the bar drains. The client animates from it, so the sign is not
    /// decoration — a positive scale draws a bar filling up while the player suffocates.
    /// </remarks>
    [Fact]
    public void GoingUnder_StartsTheBreathBar()
    {
        PlayerEnvironment environment = Submerged();

        EnvironmentUpdate update = environment.Update(100, MaxHealth, Level, true, NoScatter);

        MirrorTimerUpdate timer = Assert.Single(update.Timers);

        Assert.Equal(MirrorTimer.Breath, timer.Timer);
        Assert.Equal(PlayerEnvironment.BreathMs, timer.MaxMs);
        Assert.Equal(PlayerEnvironment.BreathMs, timer.CurrentMs);
        Assert.Equal(-1, timer.Scale);
        Assert.False(timer.Stop);
        Assert.Empty(update.Hits);
    }

    /// <summary>Staying under drains the bar without saying anything more.</summary>
    /// <remarks>
    /// A packet per tick would be about twenty a second, and the client redraws from each one — the
    /// bar visibly stutters. The client animates the countdown itself from the scale it was given.
    /// </remarks>
    [Fact]
    public void StayingUnder_DrainsTheBarSilently()
    {
        PlayerEnvironment environment = Submerged();

        environment.Update(100, MaxHealth, Level, true, NoScatter);

        for (int i = 0; i < 10; i++)
        {
            environment.Refresh(UnderWater, isAlive: true);
            EnvironmentUpdate update = environment.Update(1000, MaxHealth, Level, true, NoScatter);

            Assert.Empty(update.Timers);
            Assert.Empty(update.Hits);
        }

        Assert.Equal(PlayerEnvironment.BreathMs - 10_000, environment.Remaining(MirrorTimer.Breath));
    }

    /// <summary>
    /// Once the breath runs out, the player drowns once a second.
    /// </summary>
    /// <remarks>
    /// Once a <i>second</i>, not once a tick — the expiry adds a second back rather than resetting
    /// to zero. Getting that wrong drowns a player twenty times faster than it should, which is
    /// instantly fatal instead of merely dangerous.
    /// </remarks>
    [Fact]
    public void WhenTheBreathRunsOut_ThePlayerDrownsOncePerSecond()
    {
        PlayerEnvironment environment = Submerged();

        environment.Update(100, MaxHealth, Level, true, NoScatter);
        Drain(environment, PlayerEnvironment.BreathMs);

        // Twenty ticks of 100 ms is two seconds, so two helpings of damage.
        int helpings = 0;

        for (int i = 0; i < 20; i++)
        {
            environment.Refresh(UnderWater, isAlive: true);
            helpings += environment.Update(100, MaxHealth, Level, true, NoScatter).Hits.Count;
        }

        Assert.Equal(2, helpings);
    }

    /// <summary>Drowning costs a fifth of maximum health, plus a scatter of up to the player's level.</summary>
    [Fact]
    public void DrowningDamage_IsAFifthOfMaximumHealth()
    {
        PlayerEnvironment environment = Submerged();

        environment.Update(100, MaxHealth, Level, true, NoScatter);
        Drain(environment, PlayerEnvironment.BreathMs);

        environment.Refresh(UnderWater, isAlive: true);
        EnvironmentalHit hit = Assert.Single(environment.Update(1000, MaxHealth, Level, true, NoScatter).Hits);

        Assert.Equal(EnvironmentalDamageType.Drowning, hit.Type);
        Assert.Equal(MaxHealth / 5, hit.Amount);
    }

    /// <summary>
    /// Surfacing refills the bar ten times faster than it drained, then removes it.
    /// </summary>
    /// <remarks>
    /// The stop is what takes the bar off the screen. Without it the client keeps drawing a
    /// half-empty breath meter on a player standing on dry land.
    /// </remarks>
    [Fact]
    public void Surfacing_RefillsThenStopsTheBar()
    {
        PlayerEnvironment environment = Submerged();

        environment.Update(100, MaxHealth, Level, true, NoScatter);

        // Ten seconds under, so 10s to claw back at 10x — one second of real time.
        Drain(environment, 10_000);

        environment.Refresh(LiquidData.None, isAlive: true);
        EnvironmentUpdate first = environment.Update(100, MaxHealth, Level, true, NoScatter);

        MirrorTimerUpdate refilling = Assert.Single(first.Timers);
        Assert.Equal(PlayerEnvironment.RegenScale, refilling.Scale);
        Assert.False(refilling.Stop);

        // Keep surfacing until it is full.
        MirrorTimerUpdate? stopped = null;

        for (int i = 0; i < 100 && stopped is null; i++)
        {
            environment.Refresh(LiquidData.None, isAlive: true);

            foreach (MirrorTimerUpdate timer in environment.Update(100, MaxHealth, Level, true, NoScatter).Timers)
            {
                if (timer.Stop)
                {
                    stopped = timer;
                }
            }
        }

        Assert.NotNull(stopped);
        Assert.Equal(MirrorTimer.Breath, stopped!.Value.Timer);
        Assert.Equal(PlayerEnvironment.Disabled, environment.Remaining(MirrorTimer.Breath));
    }

    /// <summary>
    /// Wading is not drowning.
    /// </summary>
    /// <remarks>
    /// The breath bar needs the player fully under, and standing waist-deep is <c>InWater</c>
    /// rather than <c>UnderWater</c>. Keying the bar on "touching liquid" drowns anyone paddling.
    /// </remarks>
    [Fact]
    public void Wading_DoesNotStartTheBreathBar()
    {
        PlayerEnvironment environment = new();

        environment.Refresh(
            new LiquidData(1, LiquidTypeMask.Water, 10f, 0f, LiquidStatus.InWater), isAlive: true);

        Assert.Empty(environment.Update(1000, MaxHealth, Level, true, NoScatter).Timers);
    }

    /// <summary>Deep water exhausts as well as drowns, and the two bars are independent.</summary>
    [Fact]
    public void DarkWater_StartsTheFatigueBarAsWell()
    {
        PlayerEnvironment environment = new();

        environment.Refresh(
            new LiquidData(2, LiquidTypeMask.Ocean | LiquidTypeMask.DarkWater, 10f, -50f, LiquidStatus.UnderWater),
            isAlive: true);

        EnvironmentUpdate update = environment.Update(100, MaxHealth, Level, true, NoScatter);

        Assert.Contains(update.Timers, t => t.Timer == MirrorTimer.Breath);
        Assert.Contains(update.Timers, t => t.Timer == MirrorTimer.Fatigue);
        Assert.Equal(PlayerEnvironment.FatigueMs, environment.Remaining(MirrorTimer.Fatigue));
    }

    /// <summary>
    /// Lava burns on contact, for a flat amount, and never draws a bar.
    /// </summary>
    /// <remarks>
    /// Two differences from the other timers at once. It bills on <i>contact</i> rather than on
    /// submersion — ankle-deep in lava is still in lava — and the damage is a flat 600-700 rather
    /// than a share of maximum health, so it is trivial at level 80 and lethal at level 5. Upstream
    /// also never sends the bar, which is why nothing is asserted about one.
    /// </remarks>
    [Fact]
    public void Lava_BurnsOnContactForAFlatAmount()
    {
        PlayerEnvironment environment = new();

        LiquidData lava = new(3, LiquidTypeMask.Magma, 10f, 0f, LiquidStatus.WaterWalk);

        environment.Refresh(lava, isAlive: true);
        environment.Update(100, MaxHealth, Level, true, NoScatter);

        // One past the interval, not exactly on it: the expiry test is a strict "< 0", so landing
        // squarely on zero is the last tick that does not burn.
        environment.Refresh(lava, isAlive: true);
        EnvironmentUpdate update = environment.Update(
            PlayerEnvironment.FireMs + 1, MaxHealth, Level, true, NoScatter);

        EnvironmentalHit hit = Assert.Single(update.Hits);

        Assert.Equal(EnvironmentalDamageType.Lava, hit.Type);
        Assert.Equal(600u, hit.Amount);
    }

    /// <summary>Slime burns the same way, and reports itself as slime.</summary>
    [Fact]
    public void Slime_BurnsAsSlime()
    {
        PlayerEnvironment environment = new();

        LiquidData slime = new(20, LiquidTypeMask.Slime, 10f, 0f, LiquidStatus.InWater);

        environment.Refresh(slime, isAlive: true);
        environment.Update(100, MaxHealth, Level, true, NoScatter);

        environment.Refresh(slime, isAlive: true);
        EnvironmentUpdate update = environment.Update(
            PlayerEnvironment.FireMs + 1, MaxHealth, Level, true, NoScatter);

        Assert.Equal(EnvironmentalDamageType.Slime, Assert.Single(update.Hits).Type);
    }

    /// <summary>
    /// A corpse does not drown.
    /// </summary>
    /// <remarks>
    /// Ghosts run along the bottom of lakes to reach their bodies. A dead player who kept drowning
    /// would take damage they cannot see, and the bar would sit on the release screen.
    /// </remarks>
    [Fact]
    public void TheDead_DoNotDrown()
    {
        PlayerEnvironment environment = Submerged();

        environment.Update(100, MaxHealth, Level, true, NoScatter);
        Drain(environment, PlayerEnvironment.BreathMs);

        environment.Refresh(UnderWater, isAlive: false);
        EnvironmentUpdate update = environment.Update(1000, MaxHealth, Level, isAlive: false, NoScatter);

        Assert.Empty(update.Hits);
        Assert.Contains(update.Timers, t => t is { Timer: MirrorTimer.Breath, Stop: true });
    }

    // ------------------------------------------------------------------ falling

    /// <summary>A short drop costs nothing.</summary>
    /// <remarks>
    /// The floor is about thirteen and a half yards, which is roughly a two-storey building — every
    /// jump and most terrain steps are below it, and a player who took damage for hopping off a rock
    /// would notice immediately.
    /// </remarks>
    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    [InlineData(13.47f)]
    public void AShortFall_CostsNothing(float distance) =>
        Assert.Equal(0u, FallDamage.Calculate(distance, MaxHealth));

    /// <summary>
    /// Past the threshold the damage rises linearly, and a long enough fall is fatal.
    /// </summary>
    /// <remarks>
    /// The intercept is negative, so the line only crosses zero a little above the minimum distance
    /// — between 13.48 and about 13.5 yards the fall qualifies but still costs nothing. That is
    /// upstream's shape and not a rounding artefact.
    /// </remarks>
    [Fact]
    public void ALongFall_ScalesWithDistance()
    {
        uint shortish = FallDamage.Calculate(20f, MaxHealth);
        uint longer = FallDamage.Calculate(40f, MaxHealth);

        Assert.True(shortish > 0, "a twenty-yard fall should hurt");
        Assert.True(longer > shortish, $"forty yards ({longer}) should hurt more than twenty ({shortish})");
    }

    /// <summary>Damage never exceeds the player's maximum health, however far the fall.</summary>
    /// <remarks>
    /// The number is shown to the client. Uncapped, a fall from the top of the world reports several
    /// times the player's health, which reads as a bug even though the player is equally dead.
    /// </remarks>
    [Fact]
    public void AVeryLongFall_IsCappedAtMaximumHealth()
    {
        Assert.Equal(MaxHealth, FallDamage.Calculate(10_000f, MaxHealth));
    }

    /// <summary>Around seventy yards a fall kills outright from full health.</summary>
    [Fact]
    public void SeventyYards_IsFatalFromFullHealth()
    {
        Assert.Equal(MaxHealth, FallDamage.Calculate(70f, MaxHealth));
        Assert.True(FallDamage.Calculate(60f, MaxHealth) < MaxHealth, "sixty yards should be survivable");
    }

    /// <summary>Safe Fall shortens the fall before it is measured.</summary>
    [Fact]
    public void SafeFall_ReducesTheMeasuredDistance()
    {
        uint plain = FallDamage.Calculate(40f, MaxHealth);
        uint cushioned = FallDamage.Calculate(40f, MaxHealth, safeFallReduction: 20);

        Assert.True(cushioned < plain, $"safe fall should soften the landing: {cushioned} vs {plain}");
    }

    // ------------------------------------------------------------------ helpers

    private static LiquidData UnderWater =>
        new(1, LiquidTypeMask.Water, 10f, -20f, LiquidStatus.UnderWater);

    private static PlayerEnvironment Submerged()
    {
        PlayerEnvironment environment = new();
        environment.Refresh(UnderWater, isAlive: true);

        return environment;
    }

    /// <summary>Holds the player under for a while, in one-second ticks.</summary>
    private static void Drain(PlayerEnvironment environment, int milliseconds)
    {
        for (int elapsed = 0; elapsed < milliseconds; elapsed += 1000)
        {
            environment.Refresh(UnderWater, isAlive: true);
            environment.Update(1000, MaxHealth, Level, true, NoScatter);
        }
    }
}
