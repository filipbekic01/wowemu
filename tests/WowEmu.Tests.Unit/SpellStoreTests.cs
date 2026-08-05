using WowEmu.Data.Client;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// <c>Spell.dbc</c> and the four tables it points into.
/// </summary>
/// <remarks>
/// Almost everything here is checked against spells whose numbers are a matter of record — Fireball
/// hits for 14-22, Frostbolt slows by 40 %. That is the only real oracle for a 234-column file: the
/// format string can be wrong in a way that still parses, and the symptom is a spell that works but
/// for the wrong amount.
/// </remarks>
public sealed class SpellStoreTests(ITestOutputHelper output)
{
    private static SpellStores Load() => SpellStores.Load(ClientData.DbcDirectory);

    [RequiresClientDataFact]
    public void EveryStore_Loads()
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.Count > 40_000, $"only {spells.Spells.Count} spells");
        Assert.True(spells.CastTimes.Count > 0);
        Assert.True(spells.Ranges.Count > 0);
        Assert.True(spells.Durations.Count > 0);
        Assert.True(spells.Radii.Count > 0);

        output.WriteLine(
            $"{spells.Spells.Count} spells, {spells.CastTimes.Count} cast times, " +
            $"{spells.Ranges.Count} ranges, {spells.Durations.Count} durations, {spells.Radii.Count} radii");
    }

    /// <summary>
    /// Every spell has a name, which is the cheapest proof the string block is aligned.
    /// </summary>
    /// <remarks>
    /// A string column is an offset into a separate block. One field out of place gives an offset
    /// into the middle of some other spell's name — so garbage, or nothing, rather than an error.
    /// </remarks>
    [RequiresClientDataFact]
    public void EverySpell_HasAName()
    {
        SpellStores spells = Load();

        int unnamed = spells.Spells.Entries.Count(spell => string.IsNullOrEmpty(spell.Name));

        Assert.Equal(0, unnamed);
    }

    /// <summary>
    /// Fireball reads exactly as the game has it.
    /// </summary>
    /// <remarks>
    /// The reference case: a rank 1 Fireball is 14-22 fire damage over a 1.5 second cast at 35
    /// yards, and leaves a 4-second burn ticking every 2. Every one of those numbers comes from a
    /// different part of the record, so all of them agreeing means the layout is right.
    /// </remarks>
    [RequiresClientDataFact]
    public void Fireball_ReadsAsTheGameHasIt()
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.TryGet(133, out SpellEntry fireball));

        Assert.Equal("Fireball", fireball.Name);
        Assert.Equal("Rank 1", fireball.Rank);

        // School mask 4 is fire; 1 is physical, 2 holy, 8 nature, 16 frost, 32 shadow, 64 arcane.
        Assert.Equal(4u, fireball.SchoolMask);

        Assert.Equal(1500, spells.CastTimeMs(fireball));
        Assert.Equal(35f, spells.MaxRange(fireball));

        // Direct damage, then the burn.
        SpellEffectEntry damage = fireball.Effects[0];

        Assert.Equal(2u, damage.Effect);   // SPELL_EFFECT_SCHOOL_DAMAGE
        Assert.Equal(14, damage.MinValue);
        Assert.Equal(22, damage.MaxValue);

        SpellEffectEntry burn = fireball.Effects[1];

        Assert.Equal(6u, burn.Effect);      // SPELL_EFFECT_APPLY_AURA
        Assert.Equal(3u, burn.ApplyAuraName);   // SPELL_AURA_PERIODIC_DAMAGE
        Assert.Equal(2000u, burn.Amplitude);
        Assert.Equal(4000, spells.DurationMs(fireball));
    }

    /// <summary>
    /// <c>BasePoints</c> is stored one <i>below</i> the minimum.
    /// </summary>
    /// <remarks>
    /// The single most likely way to get this file subtly wrong. The client rolls
    /// <c>base + 1 … base + sides</c>, so Fireball's 14-22 is stored as base 13 with 9 sides.
    /// Reading the column as the minimum makes every spell in the game hit for one less — uniform,
    /// plausible, and undetectable without something to compare against.
    /// </remarks>
    [RequiresClientDataFact]
    public void BasePoints_IsOneBelowTheMinimum()
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.TryGet(133, out SpellEntry fireball));

        SpellEffectEntry damage = fireball.Effects[0];

        Assert.Equal(13, damage.BasePoints);
        Assert.Equal(9, damage.DieSides);

        Assert.Equal(damage.BasePoints + 1, damage.MinValue);
        Assert.Equal(damage.BasePoints + damage.DieSides, damage.MaxValue);
    }

    /// <summary>A single-value effect has one side, so its range collapses to a point.</summary>
    [RequiresClientDataFact]
    public void ASingleValueEffect_HasNoSpread()
    {
        SpellStores spells = Load();

        // Corruption's periodic tick is a flat 10, not a range.
        Assert.True(spells.Spells.TryGet(172, out SpellEntry corruption));

        SpellEffectEntry tick = corruption.Effects[0];

        Assert.Equal(1, tick.DieSides);
        Assert.Equal(tick.MinValue, tick.MaxValue);
        Assert.Equal(10, tick.MinValue);
    }

    /// <summary>
    /// Costs come from two different columns depending on the power type.
    /// </summary>
    /// <remarks>
    /// Wrath moved caster spells to a percentage of base mana and left rage and energy as flat
    /// numbers, so a server that reads only <c>ManaCost</c> finds every mage spell free. Rage is
    /// stored ten times its displayed value, which is why Heroic Strike's 15 rage reads as 150.
    /// </remarks>
    [RequiresClientDataFact]
    public void SpellCosts_ComeFromTwoDifferentColumns()
    {
        SpellStores spells = Load();

        // Fireball: 8 % of base mana, nothing in the flat column.
        Assert.True(spells.Spells.TryGet(133, out SpellEntry fireball));

        Assert.Equal(0u, fireball.ManaCost);
        Assert.Equal(8u, fireball.ManaCostPercentage);
        Assert.Equal(0u, fireball.PowerType);   // POWER_MANA

        // Heroic Strike: 15 rage, stored as 150.
        Assert.True(spells.Spells.TryGet(78, out SpellEntry heroicStrike));

        Assert.Equal(150u, heroicStrike.ManaCost);
        Assert.Equal(0u, heroicStrike.ManaCostPercentage);
        Assert.Equal(1u, heroicStrike.PowerType);   // POWER_RAGE

        // Eviscerate: 35 energy, flat.
        Assert.True(spells.Spells.TryGet(2098, out SpellEntry eviscerate));

        Assert.Equal(35u, eviscerate.ManaCost);
        Assert.Equal(3u, eviscerate.PowerType);   // POWER_ENERGY
    }

    /// <summary>The school masks are the ones the damage system uses.</summary>
    [RequiresClientDataTheory]
    [InlineData(78u, 1u)]     // Heroic Strike — physical
    [InlineData(585u, 2u)]    // Smite — holy
    [InlineData(133u, 4u)]    // Fireball — fire
    [InlineData(116u, 16u)]   // Frostbolt — frost
    [InlineData(172u, 32u)]   // Corruption — shadow
    public void TheSchoolMasks_AreTheOnesDamageUses(uint spellId, uint expectedSchool)
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.TryGet(spellId, out SpellEntry spell));
        Assert.Equal(expectedSchool, spell.SchoolMask);
    }

    /// <summary>
    /// A melee ability is marked as one, and a cast spell is not.
    /// </summary>
    /// <remarks>
    /// <c>DmgClass</c> decides which mitigation applies — a physical ability goes through armour and
    /// the melee attack table, a magic one through resistance. Getting it backwards makes armour
    /// reduce spell damage.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheDamageClass_SeparatesMeleeFromMagic()
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.TryGet(78, out SpellEntry heroicStrike));
        Assert.True(spells.Spells.TryGet(133, out SpellEntry fireball));

        Assert.Equal(2u, heroicStrike.DmgClass);   // SPELL_DAMAGE_CLASS_MELEE
        Assert.Equal(1u, fireball.DmgClass);       // SPELL_DAMAGE_CLASS_MAGIC
    }

    /// <summary>
    /// Cast time, range and duration are indices into other files, not values.
    /// </summary>
    /// <remarks>
    /// Instant spells have <c>CastingTimeIndex = 1</c>, and 1 is instant because row 1 of
    /// <c>SpellCastTimes.dbc</c> says zero — not because the index means anything by itself. Using
    /// an index where a number belongs gives a Fireball that takes one millisecond.
    /// </remarks>
    [RequiresClientDataFact]
    public void CastTimeAndRange_AreResolvedThroughTheirOwnTables()
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.TryGet(133, out SpellEntry fireball));

        // The index is small; the resolved value is not.
        Assert.True(fireball.CastingTimeIndex < 100);
        Assert.Equal(1500, spells.CastTimeMs(fireball));

        Assert.True(fireball.RangeIndex < 100);
        Assert.Equal(35f, spells.MaxRange(fireball));

        // Corruption is instant despite having a cast-time index.
        Assert.True(spells.Spells.TryGet(172, out SpellEntry corruption));
        Assert.Equal(0, spells.CastTimeMs(corruption));
    }

    /// <summary>An index with no row resolves to zero rather than throwing.</summary>
    /// <remarks>
    /// Index 0 is extremely common and there is no row 0. Throwing would make the store unusable on
    /// the majority of the file.
    /// </remarks>
    [RequiresClientDataFact]
    public void AMissingIndex_ResolvesToZero()
    {
        SpellStores spells = Load();

        SpellEntry nothing = new(
            0, "", "", new uint[SpellEntry.AttributeWords],
            Category: 0, CastingTimeIndex: 0, RecoveryTime: 0, CategoryRecoveryTime: 0,
            StartRecoveryCategory: 0, StartRecoveryTime: 0, InterruptFlags: 0, Targets: 0,
            PowerType: 0, ManaCost: 0, ManaCostPerLevel: 0, ManaCostPercentage: 0,
            RangeIndex: 0, Speed: 0f, DurationIndex: 0, BaseLevel: 0, SpellLevel: 0, MaxLevel: 0,
            SchoolMask: 0, DmgClass: 0, PreventionType: 0, SpellFamilyName: 0,
            MaxAffectedTargets: 0, SpellIconId: 0, SpellVisual: 0,
            Effects: new SpellEffectEntry[SpellConstants.MaxEffects]);

        Assert.Equal(0, spells.CastTimeMs(nothing));
        Assert.Equal(0f, spells.MaxRange(nothing));
        Assert.Equal(0, spells.DurationMs(nothing));
        Assert.Equal(0, spells.MaxDurationMs(nothing));
    }

    /// <summary>
    /// A duration of −1 means "until dispelled" and must not be clamped.
    /// </summary>
    /// <remarks>
    /// Clamping it to zero turns every permanent aura into one that expires the instant it is
    /// applied, which reads as auras not working at all rather than as a sign error.
    /// </remarks>
    [RequiresClientDataFact]
    public void APermanentDuration_StaysNegative()
    {
        SpellStores spells = Load();

        SpellDurationEntry permanent = spells.Durations.Entries.First(entry => entry.Base < 0);

        Assert.True(permanent.Base < 0);

        output.WriteLine($"permanent duration row {permanent.Id}: base {permanent.Base}");
    }

    /// <summary>
    /// The per-level duration column is not applied, because Wrath does not apply it.
    /// </summary>
    /// <remarks>
    /// A vanilla remnant. Scaling by it is the reasonable-looking reading and is measurably wrong:
    /// several rows carry a base of 100,000 seconds against a maximum of 15, so anything that scaled
    /// and clamped would sit at the maximum from level 1 and never move. Only one row in the file
    /// even has a maximum above its base.
    /// </remarks>
    [RequiresClientDataFact]
    public void ThePerLevelDurationColumn_IsNotApplied()
    {
        SpellStores spells = Load();

        SpellDurationEntry scaled = spells.Durations.Entries.First(entry => entry.PerLevel > 0);

        SpellEntry spell = new(
            1, "test", "", new uint[SpellEntry.AttributeWords],
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0f,
            DurationIndex: scaled.Id, BaseLevel: 1, SpellLevel: 1, MaxLevel: 0,
            0, 0, 0, 0, 0, 0, 0,
            Effects: new SpellEffectEntry[SpellConstants.MaxEffects]);

        // The same answer whatever level is asked about, because level is not an input.
        Assert.Equal(Math.Abs(scaled.Base), spells.DurationMs(spell));
        Assert.Equal(Math.Abs(scaled.Max), spells.MaxDurationMs(spell));

        // And the data itself says why: bases far above their own maxima.
        int inverted = spells.Durations.Entries.Count(entry => entry.PerLevel > 0 && entry.Max < entry.Base);

        Assert.True(inverted > 0, "no inverted rows, so the reasoning above needs rechecking");

        output.WriteLine($"{inverted} level-scaled rows have a base above their maximum");
        output.WriteLine($"  e.g. row {scaled.Id}: base {scaled.Base}, +{scaled.PerLevel}/level, max {scaled.Max}");
    }

    /// <summary>
    /// Effects live in three parallel blocks, not three interleaved records.
    /// </summary>
    /// <remarks>
    /// Every per-effect column is a run of three consecutive fields — all three <c>Effect</c>s, then
    /// all three <c>DieSides</c>. Reading them as one record per effect gives effect 0 the right
    /// values and effects 1 and 2 fields belonging to something else entirely.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheEffectSlots_AreParallelBlocks()
    {
        SpellStores spells = Load();

        // Frostbolt: a slow in slot 0, the damage in slot 1, an unrelated aura in slot 2. If the
        // slots were being read interleaved, slot 1 would not be a coherent damage effect.
        Assert.True(spells.Spells.TryGet(116, out SpellEntry frostbolt));

        Assert.Equal(3, frostbolt.Effects.Length);

        Assert.Equal(6u, frostbolt.Effects[0].Effect);        // apply aura
        Assert.Equal(33u, frostbolt.Effects[0].ApplyAuraName);  // MOD_DECREASE_SPEED
        Assert.Equal(-40, frostbolt.Effects[0].MinValue);       // 40 % slower — and base+1 again

        Assert.Equal(2u, frostbolt.Effects[1].Effect);        // school damage
        Assert.Equal(18, frostbolt.Effects[1].MinValue);
        Assert.Equal(20, frostbolt.Effects[1].MaxValue);

        Assert.Equal(3, frostbolt.UsedEffects.Count());
    }

    /// <summary>An unused effect slot is zero throughout, and reports itself unused.</summary>
    [RequiresClientDataFact]
    public void AnUnusedEffectSlot_SaysSo()
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.TryGet(585, out SpellEntry smite));

        Assert.True(smite.Effects[0].IsUsed);
        Assert.False(smite.Effects[1].IsUsed);
        Assert.False(smite.Effects[2].IsUsed);

        Assert.Single(smite.UsedEffects);
    }

    /// <summary>Attributes are eight separate words, and the passive bit is in the first.</summary>
    [RequiresClientDataFact]
    public void TheAttributeWords_AreEightAndIndexedFromZero()
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.TryGet(133, out SpellEntry fireball));

        Assert.Equal(SpellEntry.AttributeWords, fireball.Attributes.Length);
        Assert.False(fireball.IsPassive, "Fireball is not a passive");

        int passives = spells.Spells.Entries.Count(spell => spell.IsPassive);

        // Thousands of passives exist — talents, racials, item procs. Zero would mean the bit is
        // being read from the wrong word.
        Assert.True(passives > 1000, $"only {passives} passive spells, which suggests a wrong word");

        output.WriteLine($"{passives} passive spells of {spells.Spells.Count}");
    }

    /// <summary>Out-of-range attribute words are a false answer, not an exception.</summary>
    [Fact]
    public void AnAttributeWordPastTheEnd_IsFalse()
    {
        SpellEntry spell = new(
            1, "test", "", [0xFFFFFFFF, 0, 0, 0, 0, 0, 0, 0],
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            new SpellEffectEntry[SpellConstants.MaxEffects]);

        Assert.True(spell.HasAttribute(0, 1));
        Assert.False(spell.HasAttribute(8, 1));
        Assert.False(spell.HasAttribute(-1, 1));
    }

    /// <summary>A spell with a rank shows it; one without does not gain empty brackets.</summary>
    [RequiresClientDataFact]
    public void TheDisplayName_IncludesTheRankOnlyWhenThereIsOne()
    {
        SpellStores spells = Load();

        Assert.True(spells.Spells.TryGet(133, out SpellEntry fireball));
        Assert.Equal("Fireball (Rank 1)", fireball.ToString());

        SpellEntry unranked = spells.Spells.Entries.First(spell => string.IsNullOrEmpty(spell.Rank));

        Assert.Equal(unranked.Name, unranked.ToString());
    }
}
