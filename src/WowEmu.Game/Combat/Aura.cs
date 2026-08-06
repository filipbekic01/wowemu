using WowEmu.Core;
using WowEmu.Data.Client;

namespace WowEmu.Game.Combat;

/// <summary>
/// What an aura effect does. <c>AuraType</c>.
/// </summary>
/// <remarks>
/// Six of upstream's 300-odd. PLAN.md §7 risk 4 caps the spell system deliberately; these are the
/// ones a damage spell, a heal-over-time and a slow need, and the rest arrive on demand.
/// </remarks>
public static class AuraType
{
    /// <summary>Damage every <c>Amplitude</c> milliseconds.</summary>
    public const uint PeriodicDamage = 3;

    /// <summary>Healing on the same schedule.</summary>
    public const uint PeriodicHeal = 8;

    /// <summary>A flat change to damage dealt.</summary>
    public const uint ModDamageDone = 13;

    /// <summary>A change to one of the five attributes.</summary>
    public const uint ModStat = 29;

    /// <summary>A percentage change to movement speed.</summary>
    public const uint ModIncreaseSpeed = 31;

    /// <inheritdoc cref="ModIncreaseSpeed"/>
    public const uint ModDecreaseSpeed = 33;

    /// <summary>Breathe under water indefinitely. Death knights, warlock pets, Water Breathing.</summary>
    public const uint WaterBreathing = 82;

    /// <summary>Slow Fall and Levitate: no fall damage at all while it lasts.</summary>
    public const uint FeatherFall = 105;

    /// <summary>Yards of a fall to ignore before it is measured. Safe Fall, the rogue talent.</summary>
    public const uint SafeFall = 144;

    /// <summary>A percentage change to how long a breath lasts.</summary>
    /// <remarks>
    /// Distinct from <see cref="WaterBreathing"/>, which removes the timer entirely rather than
    /// lengthening it — the two look interchangeable and are not.
    /// </remarks>
    public const uint ModWaterBreathing = 155;

    /// <summary>Whether this server does anything at all with a type.</summary>
    /// <remarks>
    /// An unhandled type is still applied and still shown to the client — it just has no effect. A
    /// slow you can see on your buff bar that does not slow you is worse than one that never
    /// appeared, so <see cref="AuraEffect.IsHandled"/> is what the tests assert against rather than
    /// leaving it implicit.
    /// </remarks>
    public static bool IsHandled(uint type) =>
        type is PeriodicDamage or PeriodicHeal or ModStat or ModIncreaseSpeed or ModDecreaseSpeed
            or WaterBreathing or FeatherFall or SafeFall or ModWaterBreathing;

    /// <summary>Whether a type ticks on a timer rather than applying once.</summary>
    public static bool IsPeriodic(uint type) => type is PeriodicDamage or PeriodicHeal;
}

/// <summary>Flags on an aura as the client sees it. <c>AuraFlags</c>.</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "AuraFlags is upstream's name; renaming it breaks the trail back to the C++.")]
public enum AuraFlags : byte
{
    None = 0x00,

    /// <summary>Effect slot 0 carries an amount the client should read.</summary>
    EffectIndex0 = 0x01,
    EffectIndex1 = 0x02,
    EffectIndex2 = 0x04,

    /// <summary>
    /// The target cast it on itself.
    /// </summary>
    /// <remarks>
    /// Decides whether the caster guid is on the wire at all: it is written only when this is
    /// <i>clear</i>. Setting it on someone else's debuff drops a packed guid the client is still
    /// reading, and everything after shifts.
    /// </remarks>
    Caster = 0x08,

    Positive = 0x10,

    /// <summary>Two durations follow — the maximum and what is left.</summary>
    Duration = 0x20,

    AnyEffectAmountSent = 0x40,
    Negative = 0x80,
}

/// <summary>
/// One effect of one aura on one unit.
/// </summary>
/// <remarks>
/// A spell can carry three, and they tick independently — Fireball's burn is effect 1 while its
/// direct damage is effect 0 and not an aura at all.
/// </remarks>
public sealed class AuraEffect
{
    internal AuraEffect(uint type, int amount, uint amplitudeMs, int effectIndex, int miscValue)
    {
        Type = type;
        Amount = amount;
        AmplitudeMs = amplitudeMs;
        EffectIndex = effectIndex;
        MiscValue = miscValue;

        // The first tick is a full period away, not immediate. A damage-over-time that ticks the
        // instant it lands is a direct hit with extra steps, and it front-loads every such spell.
        PeriodicTimerMs = (int)amplitudeMs;
    }

    /// <summary>Which <see cref="AuraType"/> this is.</summary>
    public uint Type { get; }

    /// <summary>How much, per tick for a periodic one.</summary>
    public int Amount { get; }

    /// <summary>Milliseconds between ticks. Zero for a non-periodic effect.</summary>
    public uint AmplitudeMs { get; }

    /// <summary>Which of the spell's three effect slots this came from.</summary>
    public int EffectIndex { get; }

    /// <summary>
    /// What the effect is about, when its type alone does not say.
    /// </summary>
    /// <remarks>
    /// Meaning depends entirely on <see cref="Type"/>: for a stat modifier it is which attribute,
    /// for a school modifier which school. Read straight from the spell rather than interpreted
    /// here, because interpreting it needs to know the type and only the consumer does.
    /// </remarks>
    public int MiscValue { get; }

    /// <summary>Milliseconds until the next tick.</summary>
    public int PeriodicTimerMs { get; private set; }

    /// <summary>How many times it has ticked.</summary>
    public uint TickCount { get; private set; }

    /// <summary>Whether this server acts on the type, as opposed to merely showing it.</summary>
    public bool IsHandled => AuraType.IsHandled(Type);

    /// <summary>Whether it ticks at all.</summary>
    public bool IsPeriodic => AuraType.IsPeriodic(Type) && AmplitudeMs > 0;

    /// <summary>
    /// Advances the timer and reports how many ticks came due.
    /// </summary>
    /// <remarks>
    /// A <c>while</c>, not an <c>if</c>: a tick longer than the amplitude owes more than one, and an
    /// <c>if</c> silently drops the rest. <b>The remainder carries forward</b> — the timer is
    /// increased by the amplitude rather than reset to it, so a fast tick does not shorten the
    /// period and the aura's total tick count comes out right.
    /// </remarks>
    /// <param name="totalTicks">The ceiling, so a dying aura cannot tick past its own duration.</param>
    internal int Advance(uint diffMs, uint totalTicks)
    {
        if (!IsPeriodic)
        {
            return 0;
        }

        PeriodicTimerMs -= (int)diffMs;

        int due = 0;

        while (PeriodicTimerMs <= 0)
        {
            if (TickCount + 1 > totalTicks)
            {
                break;
            }

            TickCount++;
            PeriodicTimerMs += (int)AmplitudeMs;
            due++;
        }

        return due;
    }
}

/// <summary>
/// One aura on one unit: a spell's worth of effects, a duration and a slot.
/// </summary>
public sealed class Aura
{
    internal Aura(SpellEntry spell, ObjectGuid caster, byte casterLevel, int durationMs, IReadOnlyList<AuraEffect> effects)
    {
        Spell = spell;
        CasterGuid = caster;
        CasterLevel = casterLevel;
        MaxDurationMs = durationMs;
        RemainingMs = durationMs;
        Effects = effects;
    }

    public SpellEntry Spell { get; }

    public ObjectGuid CasterGuid { get; }

    /// <summary>The caster's level when it landed, which the client shows on the tooltip.</summary>
    public byte CasterLevel { get; }

    /// <summary>How long it lasts. <c>-1</c> is "until dispelled".</summary>
    public int MaxDurationMs { get; }

    /// <summary>How long is left. Never falls below zero, and stays at <c>-1</c> for a permanent one.</summary>
    public int RemainingMs { get; private set; }

    public IReadOnlyList<AuraEffect> Effects { get; }

    /// <summary>Which of the target's aura slots this occupies, for the client.</summary>
    public byte Slot { get; internal set; }

    /// <summary>How many are stacked. One until stacking exists.</summary>
    public byte StackAmount { get; internal set; } = 1;

    /// <summary>Whether it lasts until something removes it.</summary>
    public bool IsPermanent => MaxDurationMs < 0;

    /// <summary>Whether its time is up.</summary>
    public bool IsExpired => !IsPermanent && RemainingMs <= 0;

    /// <summary>
    /// How many times a periodic effect may tick over the whole duration.
    /// </summary>
    /// <remarks>
    /// The cap the tick loop stops at. Without it a long tick at the end of an aura's life can fire
    /// one more time than the duration paid for.
    /// </remarks>
    public uint TotalTicks(AuraEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);

        if (IsPermanent || effect.AmplitudeMs == 0)
        {
            return uint.MaxValue;
        }

        return (uint)(MaxDurationMs / effect.AmplitudeMs);
    }

    /// <summary>Counts the duration down. Permanent auras are left alone.</summary>
    internal void Age(uint diffMs)
    {
        if (!IsPermanent)
        {
            RemainingMs = Math.Max(RemainingMs - (int)diffMs, 0);
        }
    }

    /// <summary>The flags the client is sent.</summary>
    /// <remarks>
    /// <see cref="AuraFlags.Duration"/> is set only when there is one, because it is what tells the
    /// client two more words follow. Claiming a duration a permanent aura does not have leaves the
    /// client reading the next aura's slot as a timer.
    /// </remarks>
    public AuraFlags FlagsFor(ObjectGuid target)
    {
        AuraFlags flags = AuraFlags.EffectIndex0 | AuraFlags.EffectIndex1 | AuraFlags.EffectIndex2;

        if (CasterGuid == target)
        {
            flags |= AuraFlags.Caster;
        }

        if (MaxDurationMs > 0)
        {
            flags |= AuraFlags.Duration;
        }

        return flags;
    }
}

/// <summary>What one tick of a unit's auras produced.</summary>
/// <param name="Aura">Which aura.</param>
/// <param name="Effect">Which of its effects.</param>
/// <param name="Ticks">How many times it came due this tick — usually one.</param>
public readonly record struct AuraTick(Aura Aura, AuraEffect Effect, int Ticks);

/// <summary>
/// Every aura on one unit.
/// </summary>
/// <remarks>
/// Port of the container half of <c>Unit</c>'s aura handling. No stacking, no dispel, no charges,
/// no proc triggers — those need a great deal more of the spell system than M6 does.
/// <para>
/// <b>3.3.5a has no aura update fields.</b> Earlier clients carried them in the unit's block;
/// this one is told about auras through <c>SMSG_AURA_UPDATE</c> alone, keyed by slot. That is why
/// slots are allocated and tracked here rather than being an index into anything.
/// </para>
/// </remarks>
public sealed class AuraContainer
{
    /// <summary>The client's own ceiling on aura slots.</summary>
    public const byte MaxSlots = 255;

    private readonly List<Aura> _auras = [];

    public IReadOnlyList<Aura> Auras => _auras;

    public int Count => _auras.Count;

    /// <summary>Whether a spell has an aura here from anyone.</summary>
    public bool Has(uint spellId) => _auras.Exists(a => a.Spell.Id == spellId);

    /// <summary>
    /// Whether anything here carries an effect of a type. <c>HasAuraType</c>.
    /// </summary>
    /// <remarks>
    /// For the effects that are on or off rather than a quantity — water breathing, feather fall.
    /// Asking <see cref="Total"/> and comparing to zero would answer wrongly for an effect whose
    /// amount is legitimately zero, which most of the flag-shaped ones are.
    /// </remarks>
    public bool HasType(uint type) => _auras.Exists(a => AnyEffectIs(a, type));

    private static bool AnyEffectIs(Aura aura, uint type)
    {
        foreach (AuraEffect effect in aura.Effects)
        {
            if (effect.Type == type)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The aura a given caster's spell put here, if any.</summary>
    public Aura? Find(uint spellId, ObjectGuid caster) =>
        _auras.Find(a => a.Spell.Id == spellId && a.CasterGuid == caster);

    /// <summary>
    /// The largest positive amount any effect of a type carries. <c>GetMaxPositiveAuraModifier</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not a sum.</b> Two speed buffs do not add — upstream takes the strongest and ignores the
    /// rest, which is why Sprint and a mount do not multiply into something absurd. Summing here
    /// looks more natural and is a different game.
    /// </remarks>
    public int MaxPositive(uint type, int? miscValue = null)
    {
        int best = 0;

        foreach (AuraEffect effect in EffectsOf(type, miscValue))
        {
            if (effect.Amount > best)
            {
                best = effect.Amount;
            }
        }

        return best;
    }

    /// <summary>
    /// The largest negative amount any effect of a type carries. <c>GetMaxNegativeAuraModifier</c>.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="MaxPositive"/>, and the same rule: the strongest slow applies, not
    /// the total. Three 30% slows leave a target at 70% speed, not at nothing.
    /// </remarks>
    public int MaxNegative(uint type, int? miscValue = null)
    {
        int worst = 0;

        foreach (AuraEffect effect in EffectsOf(type, miscValue))
        {
            if (effect.Amount < worst)
            {
                worst = effect.Amount;
            }
        }

        return worst;
    }

    /// <summary>
    /// Every effect of a type added together. <c>GetTotalAuraModifier</c>.
    /// </summary>
    /// <remarks>
    /// This one <i>does</i> sum, and the difference from <see cref="MaxPositive"/> is per aura type
    /// rather than a matter of taste: stat buffs stack, speed buffs do not.
    /// </remarks>
    public int Total(uint type, int? miscValue = null)
    {
        int total = 0;

        foreach (AuraEffect effect in EffectsOf(type, miscValue))
        {
            total += effect.Amount;
        }

        return total;
    }

    /// <summary>
    /// Every effect of a type multiplied together as percentages. <c>GetTotalAuraMultiplier</c>.
    /// </summary>
    /// <remarks>
    /// Starts at 1.0 and multiplies in <c>1 + amount/100</c> for each, so two 10% buffs give 1.21
    /// rather than 1.20.
    /// </remarks>
    public float TotalMultiplier(uint type, int? miscValue = null)
    {
        float multiplier = 1.0f;

        foreach (AuraEffect effect in EffectsOf(type, miscValue))
        {
            multiplier *= 1.0f + (effect.Amount / 100.0f);
        }

        return multiplier;
    }

    /// <summary>
    /// The effects of a type, optionally narrowed to one <c>MiscValue</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="miscValue"/> is what makes a stat modifier answerable: the type says "changes
    /// an attribute" and the misc value says which one. A stored <c>-1</c> means every attribute at
    /// once — Mark of the Wild — so it matches any value asked for.
    /// </remarks>
    private IEnumerable<AuraEffect> EffectsOf(uint type, int? miscValue)
    {
        foreach (Aura aura in _auras)
        {
            foreach (AuraEffect effect in aura.Effects)
            {
                if (effect.Type != type)
                {
                    continue;
                }

                if (miscValue is { } wanted && effect.MiscValue != wanted && effect.MiscValue != AllStats)
                {
                    continue;
                }

                yield return effect;
            }
        }
    }

    /// <summary>A <c>MiscValue</c> of -1 on a stat modifier means all five at once.</summary>
    public const int AllStats = -1;

    /// <summary>
    /// Applies an aura, replacing the same caster's earlier one.
    /// </summary>
    /// <remarks>
    /// Refreshing rather than stacking: a second Corruption from the same warlock replaces the
    /// first, which is what the game does. Two casters each get their own, which is why the caster
    /// is part of the key.
    /// </remarks>
    /// <returns>The aura now in place, or null when the spell has no aura effects at all.</returns>
    public Aura? Apply(SpellEntry spell, ObjectGuid caster, byte casterLevel, int durationMs, Func<SpellEffectEntry, int> amountFor)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(amountFor);

        List<AuraEffect> effects = [];

        for (int index = 0; index < spell.Effects.Length; index++)
        {
            SpellEffectEntry effect = spell.Effects[index];

            if (!effect.IsUsed || effect.Effect != SpellEffectId.ApplyAura || effect.ApplyAuraName == 0)
            {
                continue;
            }

            effects.Add(new AuraEffect(
                effect.ApplyAuraName, amountFor(effect), effect.Amplitude, index, effect.MiscValue));
        }

        if (effects.Count == 0)
        {
            return null;
        }

        // Same spell from the same caster refreshes in place, keeping its slot so the client's buff
        // bar does not flicker the icon away and back.
        Aura? existing = Find(spell.Id, caster);
        byte slot = existing?.Slot ?? NextFreeSlot();

        if (existing is not null)
        {
            _auras.Remove(existing);
        }

        Aura aura = new(spell, caster, casterLevel, durationMs, effects) { Slot = slot };
        _auras.Add(aura);

        return aura;
    }

    /// <summary>Takes an aura off. Its slot becomes free for the next one.</summary>
    public bool Remove(Aura aura)
    {
        ArgumentNullException.ThrowIfNull(aura);

        return _auras.Remove(aura);
    }

    /// <summary>Removes everything. What death does.</summary>
    public void Clear() => _auras.Clear();

    /// <summary>
    /// Advances every aura by a tick.
    /// </summary>
    /// <remarks>
    /// Returns the ticks that came due and, separately, the auras that ran out — the caller applies
    /// the damage and tells the client, because neither belongs to a container of timers.
    /// <para>
    /// Expiry is checked <i>after</i> ticking, so an aura's last tick lands rather than being lost
    /// to the same diff that ended it.
    /// </para>
    /// </remarks>
    public (IReadOnlyList<AuraTick> Ticks, IReadOnlyList<Aura> Expired) Update(uint diffMs)
    {
        if (diffMs == 0 || _auras.Count == 0)
        {
            return ([], []);
        }

        List<AuraTick> ticks = [];
        List<Aura> expired = [];

        // Materialised: a tick can kill its target, and that clears the list underneath us.
        foreach (Aura aura in _auras.ToList())
        {
            foreach (AuraEffect effect in aura.Effects)
            {
                int due = effect.Advance(diffMs, aura.TotalTicks(effect));

                if (due > 0)
                {
                    ticks.Add(new AuraTick(aura, effect, due));
                }
            }

            aura.Age(diffMs);

            if (aura.IsExpired)
            {
                expired.Add(aura);
            }
        }

        foreach (Aura aura in expired)
        {
            _auras.Remove(aura);
        }

        return (ticks, expired);
    }

    /// <summary>The lowest slot nothing is using.</summary>
    /// <remarks>
    /// Lowest-free rather than a running counter: slots are a small fixed space the client indexes
    /// into, and a counter would run off the end of a long session.
    /// </remarks>
    private byte NextFreeSlot()
    {
        for (byte slot = 0; slot < MaxSlots; slot++)
        {
            if (!_auras.Exists(a => a.Slot == slot))
            {
                return slot;
            }
        }

        return MaxSlots - 1;
    }
}
