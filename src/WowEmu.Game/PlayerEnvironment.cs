using WowEmu.Data.Client;
using WowEmu.Game.Combat;

namespace WowEmu.Game;

/// <summary>
/// The three bars the client can draw under the player frame. <c>MirrorTimerType</c>.
/// </summary>
/// <remarks>
/// The numbers are the client's and are sent on the wire, so they are not free to renumber.
/// <see cref="Fire"/> is what lava and slime use; the client draws it as flames.
/// </remarks>
public enum MirrorTimer
{
    Fatigue = 0,
    Breath = 1,
    Fire = 2,
}

/// <summary>Why the world hurt someone. <c>EnviromentalDamage</c>, upstream's spelling.</summary>
public enum EnvironmentalDamageType
{
    Exhausted = 0,
    Drowning = 1,
    Fall = 2,
    Lava = 3,
    Slime = 4,
    Fire = 5,
}

/// <summary>What the player is currently standing in. <c>PlayerUnderwaterState</c>.</summary>
/// <remarks>
/// Internal to the server and never sent — the client works its own state out from the position it
/// reported. These exist so that a timer already running can tell whether the condition that started
/// it is still true.
/// </remarks>
[Flags]
public enum UnderwaterState
{
    None = 0x00,
    InWater = 0x01,
    InLava = 0x02,
    InSlime = 0x04,
    InDarkWater = 0x08,
}

/// <summary>One bar's state, as the client needs to be told it.</summary>
/// <param name="Timer">Which bar.</param>
/// <param name="MaxMs">Its full length.</param>
/// <param name="CurrentMs">How much is left.</param>
/// <param name="Scale">
/// How fast it moves, and in which direction: <c>-1</c> while it drains, <c>10</c> while it refills.
/// The client animates from this, so a bar sent with the wrong sign runs backwards.
/// </param>
/// <param name="Stop">Whether the bar should be taken away rather than drawn.</param>
public readonly record struct MirrorTimerUpdate(
    MirrorTimer Timer,
    int MaxMs,
    int CurrentMs,
    int Scale,
    bool Stop)
{
    /// <summary>The bar is gone — the player surfaced, or died.</summary>
    public static MirrorTimerUpdate Stopped(MirrorTimer timer) => new(timer, 0, 0, 0, Stop: true);
}

/// <summary>One helping of damage from the world itself.</summary>
public readonly record struct EnvironmentalHit(EnvironmentalDamageType Type, uint Amount);

/// <summary>Everything one environment tick produced.</summary>
/// <param name="Timers">Bars to start, update or stop. May be empty, and usually is.</param>
/// <param name="Hits">Damage to apply. The caller applies it, so death is noticed in one place.</param>
public readonly record struct EnvironmentUpdate(
    IReadOnlyList<MirrorTimerUpdate> Timers,
    IReadOnlyList<EnvironmentalHit> Hits)
{
    public static EnvironmentUpdate Nothing => new([], []);
}

/// <summary>
/// Drowning, fatigue and standing in lava.
/// </summary>
/// <remarks>
/// Port of <c>Player::HandleDrowning</c> and the mirror-timer half of
/// <c>Player::ProcessTerrainStatusUpdate</c>. Three independent countdowns, each started by a
/// condition, each draining while it holds and refilling ten times as fast once it stops, and each
/// dealing damage every second it spends expired.
/// <para>
/// <b>A timer at <see cref="Disabled"/> is not the same as one at zero.</b> Zero is a bar that has
/// run out and is hurting the player every second; disabled is no bar at all. Conflating them either
/// drowns someone standing on a beach or leaves a drowning player unharmed.
/// </para>
/// <para>
/// Produces a description of what happened rather than applying it. Damage is returned for the map
/// to apply for the same reason a spell's is: the one place health changes is the one place death
/// gets noticed.
/// </para>
/// </remarks>
public sealed class PlayerEnvironment
{
    /// <summary>A timer that is not running. <c>DISABLED_MIRROR_TIMER</c>.</summary>
    public const int Disabled = -1;

    /// <summary>How long a player can hold their breath. <c>CONFIG_WATER_BREATH_TIMER</c>.</summary>
    public const int BreathMs = 180_000;

    /// <summary>How long before deep water exhausts a swimmer.</summary>
    public const int FatigueMs = 60_000;

    /// <summary>How long lava waits before the next burn. Not a round number upstream either.</summary>
    public const int FireMs = 2020;

    /// <summary>How fast a bar refills once its condition stops, as a multiple of real time.</summary>
    public const int RegenScale = 10;

    private readonly int[] _timers = [Disabled, Disabled, Disabled];

    /// <summary>What the player is standing in, as of the last <see cref="Refresh"/>.</summary>
    public UnderwaterState Flags { get; private set; }

    /// <summary>What it was on the tick before. Upstream's <c>m_MirrorTimerFlagsLast</c>.</summary>
    /// <remarks>
    /// Kept so that a bar is only re-sent when the condition <i>changes</i>. Without it every tick
    /// under water sends a fresh packet, which the client redraws from — and the bar stutters.
    /// </remarks>
    public UnderwaterState PreviousFlags { get; private set; }

    /// <summary>How much of a bar is left, or <see cref="Disabled"/>.</summary>
    public int Remaining(MirrorTimer timer) => _timers[(int)timer];

    /// <summary>Whether any bar is running.</summary>
    public bool IsIdle => Flags == UnderwaterState.None && Array.TrueForAll(_timers, t => t == Disabled);

    /// <summary>
    /// Works out what the player is standing in.
    /// </summary>
    /// <remarks>
    /// Port of the mirror-timer half of <c>Player::ProcessTerrainStatusUpdate</c>. Note that each
    /// condition is only <i>cleared</i> by liquid of its own kind being absent — swimming out of
    /// lava into water clears the lava flag because the lava branch is not taken, not because the
    /// water branch cleared it.
    /// <para>
    /// Breath needs the player fully under; lava and slime need only contact, because standing
    /// ankle-deep in lava is still standing in lava.
    /// </para>
    /// </remarks>
    public void Refresh(LiquidData liquid, bool isAlive)
    {
        PreviousFlags = Flags;

        if (liquid.Status == LiquidStatus.NoWater)
        {
            Flags = UnderwaterState.None;
            return;
        }

        UnderwaterState flags = Flags;

        if ((liquid.Type & LiquidTypeMask.AllLiquids) != 0)
        {
            flags = Set(flags, UnderwaterState.InWater, (liquid.Status & LiquidStatus.UnderWater) != 0);
        }

        flags = Set(flags, UnderwaterState.InDarkWater, liquid.Type.HasFlag(LiquidTypeMask.DarkWater));

        if (liquid.Type.HasFlag(LiquidTypeMask.Magma))
        {
            flags = Set(flags, UnderwaterState.InLava, liquid.IsInContact);
        }

        if (liquid.Type.HasFlag(LiquidTypeMask.Slime))
        {
            flags = Set(flags, UnderwaterState.InSlime, liquid.IsInContact);
        }

        // A corpse does not drown. The flags stay so that a resurrected player picks up where they
        // left off, but every timer is stopped by Update below.
        if (!isAlive)
        {
            flags &= ~UnderwaterState.InWater;
        }

        Flags = flags;
    }

    /// <summary>
    /// Advances every bar and reports what came due.
    /// </summary>
    /// <param name="diff">Milliseconds of gameplay time.</param>
    /// <param name="maxHealth">The player's maximum health — drowning damage is a fifth of it.</param>
    /// <param name="level">The player's level, which adds a little scatter to the damage.</param>
    /// <param name="isAlive">A dead player's bars are stopped rather than ticked.</param>
    /// <param name="urand">The damage roll, injected so a test can make it deterministic.</param>
    /// <param name="auras">
    /// What is on the player. Optional: without it nobody can breathe under water, which is the old
    /// behaviour and correct for a player with no such buff.
    /// </param>
    public EnvironmentUpdate Update(
        uint diff,
        uint maxHealth,
        uint level,
        bool isAlive,
        Func<uint, uint, uint> urand,
        AuraContainer? auras = null)
    {
        ArgumentNullException.ThrowIfNull(urand);

        if (IsIdle)
        {
            return EnvironmentUpdate.Nothing;
        }

        List<MirrorTimerUpdate> timers = [];
        List<EnvironmentalHit> hits = [];

        Advance(
            MirrorTimer.Breath,
            UnderwaterState.InWater,
            BreathLimit(isAlive, auras),
            EnvironmentalDamageType.Drowning,
            diff, maxHealth, level, isAlive, urand, timers, hits);

        Advance(
            MirrorTimer.Fatigue,
            UnderwaterState.InDarkWater,
            FatigueMs,
            EnvironmentalDamageType.Exhausted,
            diff, maxHealth, level, isAlive, urand, timers, hits);

        AdvanceFire(diff, isAlive, urand, timers, hits);

        return new EnvironmentUpdate(timers, hits);
    }

    /// <summary>
    /// How long this player can hold their breath, or <see cref="Disabled"/> for never running out.
    /// </summary>
    /// <remarks>
    /// Port of <c>getMaxTimer(BREATH_TIMER)</c>. Two different auras, and they are not the same
    /// thing: <see cref="AuraType.WaterBreathing"/> removes the bar entirely — a death knight simply
    /// does not drown — while <see cref="AuraType.ModWaterBreathing"/> multiplies how long it lasts.
    /// Treating the second as the first gives an underwater-breathing potion that never wears off.
    /// <para>
    /// A disabled limit also stops any bar already running, because <see cref="Advance"/> takes it
    /// as "there is no such timer" — so drinking the potion mid-drown clears the bar rather than
    /// freezing it part-drained.
    /// </para>
    /// </remarks>
    private static int BreathLimit(bool isAlive, AuraContainer? auras)
    {
        if (!isAlive)
        {
            return Disabled;
        }

        if (auras is null)
        {
            return BreathMs;
        }

        if (auras.HasType(AuraType.WaterBreathing))
        {
            return Disabled;
        }

        float multiplier = auras.TotalMultiplier(AuraType.ModWaterBreathing);

        return (int)(BreathMs * multiplier);
    }

    /// <summary>
    /// Drains or refills one bar, and bills the player for every second it spends empty.
    /// </summary>
    /// <remarks>
    /// The expiry adds a second back rather than resetting to zero, which is what makes the damage
    /// arrive once per second for as long as the condition lasts instead of once per tick.
    /// </remarks>
    private void Advance(
        MirrorTimer timer,
        UnderwaterState condition,
        int maxMs,
        EnvironmentalDamageType damageType,
        uint diff,
        uint maxHealth,
        uint level,
        bool isAlive,
        Func<uint, uint, uint> urand,
        List<MirrorTimerUpdate> timers,
        List<EnvironmentalHit> hits)
    {
        int index = (int)timer;
        bool active = Flags.HasFlag(condition);
        bool wasActive = PreviousFlags.HasFlag(condition);

        if (active && maxMs != Disabled)
        {
            if (_timers[index] == Disabled)
            {
                _timers[index] = maxMs;
                timers.Add(new MirrorTimerUpdate(timer, maxMs, maxMs, -1, Stop: false));
                return;
            }

            _timers[index] -= (int)diff;

            if (_timers[index] < 0)
            {
                _timers[index] += 1000;

                if (isAlive)
                {
                    hits.Add(new EnvironmentalHit(damageType, EnvironmentalDamage(maxHealth, level, urand)));
                }
            }
            else if (!wasActive)
            {
                // The condition only just started but the bar was already part-drained — the player
                // dipped under, surfaced and went back down before it had refilled.
                timers.Add(new MirrorTimerUpdate(timer, maxMs, _timers[index], -1, Stop: false));
            }

            return;
        }

        if (_timers[index] == Disabled)
        {
            return;
        }

        // Out of the water: the bar refills ten times faster than it drained.
        _timers[index] += RegenScale * (int)diff;

        if (_timers[index] >= maxMs || !isAlive || maxMs == Disabled)
        {
            _timers[index] = Disabled;
            timers.Add(MirrorTimerUpdate.Stopped(timer));
        }
        else if (wasActive)
        {
            timers.Add(new MirrorTimerUpdate(timer, maxMs, _timers[index], RegenScale, Stop: false));
        }
    }

    /// <summary>
    /// Lava and slime, which burn on a fixed cadence rather than after a countdown.
    /// </summary>
    /// <remarks>
    /// Separate because it does not behave like the other two: the bar is never sent to the client
    /// — upstream starts and ticks <c>FIRE_TIMER</c> without a single <c>SendMirrorTimer</c> — and
    /// the damage is a flat roll rather than a share of the player's health, so a level 80 in lava
    /// takes the same 600-700 as a level 1.
    /// </remarks>
    private void AdvanceFire(
        uint diff,
        bool isAlive,
        Func<uint, uint, uint> urand,
        List<MirrorTimerUpdate> timers,
        List<EnvironmentalHit> hits)
    {
        const int Index = (int)MirrorTimer.Fire;

        bool burning = (Flags & (UnderwaterState.InLava | UnderwaterState.InSlime)) != 0;

        if (burning && isAlive)
        {
            if (_timers[Index] == Disabled)
            {
                _timers[Index] = FireMs;
                return;
            }

            _timers[Index] -= (int)diff;

            if (_timers[Index] < 0)
            {
                _timers[Index] += FireMs;

                hits.Add(new EnvironmentalHit(
                    Flags.HasFlag(UnderwaterState.InLava)
                        ? EnvironmentalDamageType.Lava
                        : EnvironmentalDamageType.Slime,
                    urand(600, 700)));
            }

            return;
        }

        if (_timers[Index] != Disabled)
        {
            _timers[Index] = Disabled;
            timers.Add(MirrorTimerUpdate.Stopped(MirrorTimer.Fire));
        }
    }

    /// <summary>
    /// What one second of drowning or exhaustion costs.
    /// </summary>
    /// <remarks>
    /// A fifth of maximum health plus a scatter of up to the player's level. Upstream carries a
    /// <c>@todo: Check this formula</c> against it, so it is reproduced rather than improved.
    /// </remarks>
    private static uint EnvironmentalDamage(uint maxHealth, uint level, Func<uint, uint, uint> urand) =>
        (maxHealth / 5) + (level > 0 ? urand(0, level - 1) : 0);

    private static UnderwaterState Set(UnderwaterState flags, UnderwaterState bit, bool on) =>
        on ? flags | bit : flags & ~bit;

    /// <summary>Stops every bar, without reporting anything. Used when a player leaves the world.</summary>
    public void Reset()
    {
        Array.Fill(_timers, Disabled);
        Flags = UnderwaterState.None;
        PreviousFlags = UnderwaterState.None;
    }
}
