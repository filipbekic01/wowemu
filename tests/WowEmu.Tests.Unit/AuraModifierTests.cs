using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game.Combat;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Auras that change a number rather than dealing damage.
/// </summary>
/// <remarks>
/// Slows, speed buffs and stat buffs were applied and shown but did nothing — a debuff you could
/// watch on your portrait while running at full speed. What is worth pinning is not the arithmetic
/// so much as <b>which auras stack and which do not</b>, because upstream answers that differently
/// per type and both answers look equally natural.
/// </remarks>
public sealed class AuraModifierTests
{
    private static readonly ObjectGuid CasterA = ObjectGuid.Create(HighGuid.Player, 1);
    private static readonly ObjectGuid CasterB = ObjectGuid.Create(HighGuid.Player, 2);

    /// <summary>
    /// Two slows do not add up — the strongest wins.
    /// </summary>
    /// <remarks>
    /// The trap. <c>GetMaxNegativeAuraModifier</c>, not a sum: three 50% slows leave a target at
    /// half speed, not at rest and not moving backwards. Summing is the obvious implementation and
    /// is a different game.
    /// </remarks>
    [Fact]
    public void TwoSlows_DoNotStack()
    {
        AuraContainer auras = new();

        Apply(auras, AuraType.ModDecreaseSpeed, -30, CasterA, id: 1);
        Apply(auras, AuraType.ModDecreaseSpeed, -50, CasterB, id: 2);

        Assert.Equal(-50, auras.MaxNegative(AuraType.ModDecreaseSpeed));
        Assert.Equal(0.5f, UnitSpeed.RateFor(auras, UnitMoveType.Run), 0.001f);
    }

    /// <summary>Nor do two speed buffs.</summary>
    [Fact]
    public void TwoSpeedBuffs_DoNotStack()
    {
        AuraContainer auras = new();

        Apply(auras, AuraType.ModIncreaseSpeed, 30, CasterA, id: 1);
        Apply(auras, AuraType.ModIncreaseSpeed, 70, CasterB, id: 2);

        Assert.Equal(1.7f, UnitSpeed.RateFor(auras, UnitMoveType.Run), 0.001f);
    }

    /// <summary>
    /// A buff and a slow multiply rather than cancelling.
    /// </summary>
    /// <remarks>
    /// +50% and −50% leave 75%, not 100%: the slow is a percentage of the already-buffed rate. The
    /// order and the compounding are both upstream's, and applying them against the base instead is
    /// the difference between a Sprinting rogue outrunning a Frostbolt and not.
    /// </remarks>
    [Fact]
    public void ABuffAndASlow_Compound()
    {
        AuraContainer auras = new();

        Apply(auras, AuraType.ModIncreaseSpeed, 50, CasterA, id: 1);
        Apply(auras, AuraType.ModDecreaseSpeed, -50, CasterB, id: 2);

        Assert.Equal(0.75f, UnitSpeed.RateFor(auras, UnitMoveType.Run), 0.001f);
    }

    /// <summary>
    /// Speed buffs reach forward movement only; slows reach everything.
    /// </summary>
    /// <remarks>
    /// Upstream's switch falls straight through for walking and the three backwards speeds, so they
    /// take debuffs and no buffs at all. A Sprinting player walks at the ordinary pace.
    /// </remarks>
    [Theory]
    [InlineData(UnitMoveType.Run, 1.5f)]
    [InlineData(UnitMoveType.Swim, 1.5f)]
    [InlineData(UnitMoveType.Flight, 1.5f)]
    [InlineData(UnitMoveType.Walk, 1.0f)]
    [InlineData(UnitMoveType.RunBack, 1.0f)]
    [InlineData(UnitMoveType.SwimBack, 1.0f)]
    public void ASpeedBuff_ReachesForwardMovementOnly(UnitMoveType type, float expected)
    {
        AuraContainer auras = new();
        Apply(auras, AuraType.ModIncreaseSpeed, 50, CasterA, id: 1);

        Assert.Equal(expected, UnitSpeed.RateFor(auras, type), 0.001f);
    }

    /// <summary>And a slow reaches every one of them.</summary>
    [Theory]
    [InlineData(UnitMoveType.Run)]
    [InlineData(UnitMoveType.Walk)]
    [InlineData(UnitMoveType.RunBack)]
    [InlineData(UnitMoveType.SwimBack)]
    public void ASlow_ReachesEverySpeed(UnitMoveType type)
    {
        AuraContainer auras = new();
        Apply(auras, AuraType.ModDecreaseSpeed, -40, CasterA, id: 1);

        Assert.Equal(0.6f, UnitSpeed.RateFor(auras, type), 0.001f);
    }

    /// <summary>A slow past 100% stops the unit rather than reversing it.</summary>
    /// <remarks>
    /// The client reads a negative speed as an immediate desync. Upstream clamps in <c>SetSpeed</c>
    /// for the same reason.
    /// </remarks>
    [Fact]
    public void ASlowPastAHundredPercent_StopsRatherThanReverses()
    {
        AuraContainer auras = new();
        Apply(auras, AuraType.ModDecreaseSpeed, -150, CasterA, id: 1);

        Assert.Equal(0f, UnitSpeed.RateFor(auras, UnitMoveType.Run), 0.001f);
    }

    /// <summary>
    /// A refresh keeps the unit's own base speeds, not the global ones.
    /// </summary>
    /// <remarks>
    /// The subtlest thing here. A creature's template scales its walk and run at spawn, so "no
    /// modifiers" is per-creature. Recomputing from the global base would give a slowed wolf a
    /// human's pace — and never give it back, because the speeds are stored rather than derived.
    /// </remarks>
    [Fact]
    public void ARefresh_KeepsTheUnitsOwnBaseSpeeds()
    {
        MovementSpeeds baseSpeeds = new() { Run = 14.0f, Walk = 5.0f };
        MovementSpeeds speeds = new() { Run = 14.0f, Walk = 5.0f };

        AuraContainer auras = new();
        Apply(auras, AuraType.ModDecreaseSpeed, -50, CasterA, id: 1);

        UnitSpeed.Refresh(speeds, baseSpeeds, auras);

        Assert.Equal(7.0f, speeds.Run, 0.001f);
        Assert.Equal(2.5f, speeds.Walk, 0.001f);
    }

    /// <summary>A refresh reports only what moved, so unchanged speeds cost no packets.</summary>
    [Fact]
    public void ARefresh_ReportsOnlyWhatChanged()
    {
        MovementSpeeds baseSpeeds = new();
        MovementSpeeds speeds = new();

        AuraContainer auras = new();

        // Nothing on the unit: nothing to say.
        Assert.Empty(UnitSpeed.Refresh(speeds, baseSpeeds, auras));

        Apply(auras, AuraType.ModIncreaseSpeed, 50, CasterA, id: 1);

        // A forward-only buff moves three of the seven.
        IReadOnlyList<UnitMoveType> changed = UnitSpeed.Refresh(speeds, baseSpeeds, auras);

        Assert.Equal(
            [UnitMoveType.Run, UnitMoveType.Swim, UnitMoveType.Flight],
            changed);

        // And a second refresh with the same auras has nothing left to report.
        Assert.Empty(UnitSpeed.Refresh(speeds, baseSpeeds, auras));
    }

    /// <summary>
    /// The slow wearing off restores the original speed exactly.
    /// </summary>
    /// <remarks>
    /// The round trip is what the base speeds exist for. Anything that recomputed from the current
    /// value would drift a little further from the truth on every application.
    /// </remarks>
    [Fact]
    public void WhenASlowExpires_TheSpeedComesBack()
    {
        MovementSpeeds baseSpeeds = new() { Run = 11.0f };
        MovementSpeeds speeds = new() { Run = 11.0f };

        AuraContainer auras = new();
        Aura slow = Apply(auras, AuraType.ModDecreaseSpeed, -60, CasterA, id: 1);

        UnitSpeed.Refresh(speeds, baseSpeeds, auras);
        Assert.Equal(4.4f, speeds.Run, 0.001f);

        auras.Remove(slow);
        UnitSpeed.Refresh(speeds, baseSpeeds, auras);

        Assert.Equal(11.0f, speeds.Run, 0.001f);
    }

    // ------------------------------------------------------------------ stats

    /// <summary>
    /// Stat buffs <i>do</i> stack, unlike speed.
    /// </summary>
    /// <remarks>
    /// The other half of the pair. Upstream uses <c>GetTotalAuraModifier</c> here and
    /// <c>GetMaxPositiveAuraModifier</c> for speed, so which rule applies is a property of the aura
    /// type rather than a convention — and picking one rule for both is wrong in one direction or
    /// the other.
    /// </remarks>
    [Fact]
    public void TwoStatBuffs_DoStack()
    {
        AuraContainer auras = new();

        Apply(auras, AuraType.ModStat, 10, CasterA, id: 1, miscValue: 0);
        Apply(auras, AuraType.ModStat, 15, CasterB, id: 2, miscValue: 0);

        Assert.Equal(25, auras.Total(AuraType.ModStat, miscValue: 0));
    }

    /// <summary>A stat buff reaches the attribute it names and no other.</summary>
    [Fact]
    public void AStatBuff_ReachesOnlyItsOwnAttribute()
    {
        AuraContainer auras = new();
        Apply(auras, AuraType.ModStat, 20, CasterA, id: 1, miscValue: 2);

        Assert.Equal(20, auras.Total(AuraType.ModStat, miscValue: 2));
        Assert.Equal(0, auras.Total(AuraType.ModStat, miscValue: 0));
    }

    /// <summary>
    /// A misc value of -1 means every attribute at once.
    /// </summary>
    /// <remarks>
    /// Mark of the Wild and the other all-stat buffs are stored that way. Reading -1 as "attribute
    /// number minus one" would index off the front of the array; treating it as its own stat gives
    /// a buff that reaches nothing.
    /// </remarks>
    [Fact]
    public void AMinusOneMiscValue_ReachesEveryAttribute()
    {
        AuraContainer auras = new();
        Apply(auras, AuraType.ModStat, 12, CasterA, id: 1, miscValue: AuraContainer.AllStats);

        for (int stat = 0; stat < 5; stat++)
        {
            Assert.Equal(12, auras.Total(AuraType.ModStat, miscValue: stat));
        }
    }

    // ------------------------------------------------------------------ helpers

    private static Aura Apply(
        AuraContainer container,
        uint type,
        int amount,
        ObjectGuid caster,
        uint id,
        int miscValue = 0)
    {
        SpellEffectEntry[] effects = new SpellEffectEntry[SpellConstants.MaxEffects];

        effects[0] = new SpellEffectEntry(
            Effect: SpellEffectId.ApplyAura,
            DieSides: 0,
            RealPointsPerLevel: 0f,
            BasePoints: amount,
            ImplicitTargetA: 0,
            ImplicitTargetB: 0,
            RadiusIndex: 0,
            ApplyAuraName: type,
            Amplitude: 0,
            ChainTarget: 0,
            ItemType: 0,
            MiscValue: miscValue,
            MiscValueB: 0,
            TriggerSpell: 0);

        SpellEntry spell = new(
            id, "test", "", new uint[SpellEntry.AttributeWords],
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0f, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, effects);

        return container.Apply(spell, caster, casterLevel: 20, durationMs: -1, e => e.BasePoints)!;
    }
}
