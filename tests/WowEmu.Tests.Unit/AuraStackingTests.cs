using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game.Combat;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Auras that stack, and auras that are used up by being triggered.
/// </summary>
/// <remarks>
/// Both come off one spell column each, and both columns are <b>zero on most spells</b> — which is
/// the trap: reading either as a limit makes every aura in the game refuse to apply or fall off the
/// first time anything happened.
/// </remarks>
public sealed class AuraStackingTests
{
    /// <summary>A non-stacking aura refreshes in place rather than piling up.</summary>
    /// <remarks>
    /// A second Corruption from the same warlock replaces the first. Zero in the column means "does
    /// not stack", not "stacks zero times".
    /// </remarks>
    [Fact]
    public void ANonStackingAura_Refreshes()
    {
        AuraContainer auras = new();
        SpellEntry spell = AuraFixture.Periodic(AuraType.ModStat, 10, amplitudeMs: 0);

        Assert.NotNull(Apply(auras, spell));
        Assert.NotNull(Apply(auras, spell));

        Assert.Equal(1, auras.Count);
        Assert.Equal(1, auras.Auras[0].StackAmount);
    }

    /// <summary>A stacking aura piles up to its limit.</summary>
    [Fact]
    public void AStackingAura_PilesUp()
    {
        AuraContainer auras = new();
        SpellEntry spell = AuraFixture.Stacking(AuraType.ModStat, 10, stackAmount: 3);

        Apply(auras, spell);
        Assert.Equal(1, auras.Auras[0].StackAmount);

        Apply(auras, spell);
        Assert.Equal(2, auras.Auras[0].StackAmount);

        Apply(auras, spell);
        Assert.Equal(3, auras.Auras[0].StackAmount);
    }

    /// <summary>
    /// And stops at the limit rather than growing past it.
    /// </summary>
    /// <remarks>
    /// At the cap only the duration moves, which is what makes a maintained debuff feel maintained
    /// rather than resetting to one stack.
    /// </remarks>
    [Fact]
    public void AStackingAura_StopsAtItsLimit()
    {
        AuraContainer auras = new();
        SpellEntry spell = AuraFixture.Stacking(AuraType.ModStat, 10, stackAmount: 3);

        for (int i = 0; i < 10; i++)
        {
            Apply(auras, spell);
        }

        Assert.Equal(1, auras.Count);
        Assert.Equal(3, auras.Auras[0].StackAmount);
    }

    /// <summary>
    /// A stack is worth its count when the totals are added up.
    /// </summary>
    /// <remarks>
    /// The half that is easy to miss: the stack count lives on the aura, and code that sums the
    /// flattened effects gives a five-stack debuff the strength of a single application.
    /// </remarks>
    [Fact]
    public void AStack_CountsItsFullStrength()
    {
        AuraContainer auras = new();
        SpellEntry spell = AuraFixture.Stacking(AuraType.ModStat, 10, stackAmount: 3);

        Apply(auras, spell);
        Assert.Equal(AuraFixture.Flat(spell.Effects[0]), auras.Total(AuraType.ModStat));

        Apply(auras, spell);
        Apply(auras, spell);

        Assert.Equal(AuraFixture.Flat(spell.Effects[0]) * 3, auras.Total(AuraType.ModStat));
    }

    /// <summary>
    /// Two casters do not stack into each other.
    /// </summary>
    /// <remarks>
    /// The caster is part of the key. Two warlocks each get their own Corruption, and one refreshing
    /// theirs must not take the other's stack with it.
    /// </remarks>
    [Fact]
    public void TwoCasters_DoNotStackIntoEachOther()
    {
        AuraContainer auras = new();
        SpellEntry spell = AuraFixture.Stacking(AuraType.ModStat, 10, stackAmount: 3);

        Apply(auras, spell, CasterOne);
        Apply(auras, spell, CasterOne);
        Apply(auras, spell, CasterTwo);

        Assert.Equal(2, auras.Count);
        Assert.Equal(2, auras.Find(spell.Id, CasterOne)!.StackAmount);
        Assert.Equal(1, auras.Find(spell.Id, CasterTwo)!.StackAmount);
    }

    // ------------------------------------------------------------------ charges

    /// <summary>An aura with charges starts with the spell's count.</summary>
    [Fact]
    public void AnAuraWithCharges_StartsFull()
    {
        AuraContainer auras = new();
        SpellEntry spell = AuraFixture.Stacking(AuraType.ModStat, 10, stackAmount: 0, charges: 3);

        Aura aura = Assert.IsType<Aura>(Apply(auras, spell));

        Assert.True(aura.HasCharges);
        Assert.Equal(3u, aura.ChargesLeft);
    }

    /// <summary>Spending the last charge says so, and only the last one.</summary>
    [Fact]
    public void SpendingTheLastCharge_SaysSo()
    {
        AuraContainer auras = new();
        SpellEntry spell = AuraFixture.Stacking(AuraType.ModStat, 10, stackAmount: 0, charges: 2);

        Aura aura = Assert.IsType<Aura>(Apply(auras, spell));

        Assert.False(aura.SpendCharge());
        Assert.Equal(1u, aura.ChargesLeft);

        Assert.True(aura.SpendCharge());
        Assert.Equal(0u, aura.ChargesLeft);
    }

    /// <summary>
    /// An aura with no charges is never spent.
    /// </summary>
    /// <remarks>
    /// Zero means unlimited, not exhausted. Returning true here would remove every timer-based buff
    /// in the game the first time anything triggered.
    /// </remarks>
    [Fact]
    public void AnAuraWithoutCharges_IsNeverSpent()
    {
        AuraContainer auras = new();
        SpellEntry spell = AuraFixture.Periodic(AuraType.ModStat, 10, amplitudeMs: 0);

        Aura aura = Assert.IsType<Aura>(Apply(auras, spell));

        Assert.False(aura.HasCharges);
        Assert.False(aura.SpendCharge());
        Assert.False(aura.SpendCharge());
    }

    private static readonly ObjectGuid CasterOne = ObjectGuid.Create(HighGuid.Player, 1);
    private static readonly ObjectGuid CasterTwo = ObjectGuid.Create(HighGuid.Player, 2);

    private static Aura? Apply(AuraContainer auras, SpellEntry spell, ObjectGuid? caster = null) =>
        auras.Apply(
            spell,
            caster ?? CasterOne,
            casterLevel: 60,
            durationMs: 60_000,
            effect => AuraFixture.Flat(effect));
}
