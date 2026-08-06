using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using WowEmu.Protocol;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>Shared spell shapes for the aura tests.</summary>
internal static class AuraFixture
{
    /// <summary>A spell carrying one periodic effect and nothing else.</summary>
    public static SpellEntry Periodic(
        uint auraType,
        int amountPerTick,
        uint amplitudeMs,
        uint id = 1,
        uint schoolMask = 4)
    {
        SpellEffectEntry[] effects = new SpellEffectEntry[SpellConstants.MaxEffects];

        effects[0] = new SpellEffectEntry(
            Effect: SpellEffectId.ApplyAura,
            DieSides: 1,
            RealPointsPerLevel: 0f,
            // Stored one below the minimum, and one side adds exactly one back — see CalcValue.
            BasePoints: amountPerTick - 1,
            ImplicitTargetA: 0,
            ImplicitTargetB: 0,
            RadiusIndex: 0,
            ApplyAuraName: auraType,
            Amplitude: amplitudeMs,
            ChainTarget: 0,
            ItemType: 0,
            MiscValue: 0,
            MiscValueB: 0,
            TriggerSpell: 0);

        return new SpellEntry(
            id, "test", "", new uint[SpellEntry.AttributeWords],
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0f, 0, 0, 0, 0,
            SchoolMask: schoolMask,
            0, 0, 0, 0, 0, 0, effects);
    }

    /// <summary>A spell with no aura effect at all, for the "nothing applies" case.</summary>
    public static SpellEntry DirectDamageOnly(uint id = 2)
    {
        SpellEffectEntry[] effects = new SpellEffectEntry[SpellConstants.MaxEffects];

        effects[0] = new SpellEffectEntry(
            SpellEffectId.SchoolDamage, 1, 0f, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        return new SpellEntry(
            id, "test", "", new uint[SpellEntry.AttributeWords],
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0f, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, effects);
    }

    /// <summary>Every effect rolls its top end, so the arithmetic under test is not a coin toss.</summary>
    public static int Flat(SpellEffectEntry effect) => effect.BasePoints + Math.Max(effect.DieSides, 0);
}

/// <summary>
/// <c>AuraEffect::Update</c>: when a periodic effect comes due.
/// </summary>
public sealed class AuraTickTests
{
    private static AuraContainer Container(
        uint auraType, int amount, uint amplitudeMs, int durationMs, out Aura aura)
    {
        AuraContainer container = new();

        aura = container.Apply(
            AuraFixture.Periodic(auraType, amount, amplitudeMs),
            new ObjectGuid(1),
            casterLevel: 20,
            durationMs,
            AuraFixture.Flat)!;

        return container;
    }

    /// <summary>
    /// The first tick is a full period away, not immediate.
    /// </summary>
    /// <remarks>
    /// A damage-over-time that ticks on landing is a direct hit with extra steps, and it would make
    /// every such spell front-loaded — visible only against a client that shows the numbers.
    /// </remarks>
    [Fact]
    public void TheFirstTick_IsAFullPeriodAway()
    {
        AuraContainer container = Container(AuraType.PeriodicDamage, 30, 3000, 12000, out _);

        Assert.Empty(container.Update(2999).Ticks);
        Assert.Single(container.Update(1).Ticks);
    }

    /// <summary>A diff longer than the amplitude owes every tick it covers, not one.</summary>
    /// <remarks>
    /// Maps out of phase are updated with four ticks' worth of accumulated diff at once, so this is
    /// the normal case rather than an edge one.
    /// </remarks>
    [Fact]
    public void ALongDiff_OwesEveryTickItCovers()
    {
        AuraContainer container = Container(AuraType.PeriodicDamage, 30, 1000, 12000, out _);

        (IReadOnlyList<AuraTick> ticks, _) = container.Update(3500);

        Assert.Single(ticks);
        Assert.Equal(3, ticks[0].Ticks);
    }

    /// <summary>
    /// The remainder carries into the next period rather than being discarded.
    /// </summary>
    /// <remarks>
    /// Resetting the timer to the amplitude instead would let a run of short diffs stretch the
    /// period, and an aura would tick fewer times than its duration paid for.
    /// </remarks>
    [Fact]
    public void TheRemainder_CarriesForward()
    {
        AuraContainer container = Container(AuraType.PeriodicDamage, 30, 1000, 12000, out _);

        // 600 + 600 crosses one second with 200 to spare; the third 600 must therefore be enough to
        // reach the second tick, which it only is if the 200 was kept.
        Assert.Empty(container.Update(600).Ticks);
        Assert.Single(container.Update(600).Ticks);
        Assert.Empty(container.Update(300).Ticks);
        Assert.Single(container.Update(600).Ticks);
    }

    /// <summary>An aura ticks exactly as many times as its duration pays for.</summary>
    [Fact]
    public void AnAura_TicksItsDurationsWorthAndNoMore()
    {
        AuraContainer container = Container(AuraType.PeriodicDamage, 30, 2000, 8000, out _);

        int ticks = 0;

        for (int i = 0; i < 20; i++)
        {
            foreach (AuraTick tick in container.Update(1000).Ticks)
            {
                ticks += tick.Ticks;
            }
        }

        Assert.Equal(4, ticks);
    }

    /// <summary>
    /// The last tick lands rather than being lost to the diff that ended the aura.
    /// </summary>
    /// <remarks>
    /// Expiry is checked after ticking for exactly this reason. Checking first drops one tick from
    /// every damage-over-time in the game — an eight-second burn paying for four ticks and landing
    /// three.
    /// </remarks>
    [Fact]
    public void TheLastTick_LandsOnTheDiffThatExpiresIt()
    {
        AuraContainer container = Container(AuraType.PeriodicDamage, 30, 2000, 4000, out _);

        Assert.Single(container.Update(2000).Ticks);

        (IReadOnlyList<AuraTick> ticks, IReadOnlyList<Aura> expired) = container.Update(2000);

        Assert.Single(ticks);
        Assert.Single(expired);
    }

    /// <summary>An expired aura is off the container, so nothing has to remove it afterwards.</summary>
    [Fact]
    public void AnExpiredAura_IsGoneFromTheContainer()
    {
        AuraContainer container = Container(AuraType.PeriodicDamage, 30, 2000, 4000, out _);

        container.Update(5000);

        Assert.Equal(0, container.Count);
    }

    /// <summary>A permanent aura never expires and never runs out of ticks.</summary>
    [Fact]
    public void APermanentAura_NeverExpires()
    {
        AuraContainer container = Container(AuraType.PeriodicDamage, 30, 1000, -1, out Aura aura);

        Assert.True(aura.IsPermanent);

        int ticks = 0;

        for (int i = 0; i < 50; i++)
        {
            foreach (AuraTick tick in container.Update(1000).Ticks)
            {
                ticks += tick.Ticks;
            }
        }

        Assert.Equal(50, ticks);
        Assert.Equal(1, container.Count);
    }

    /// <summary>A non-periodic aura never comes due, but still runs out on time.</summary>
    [Fact]
    public void ANonPeriodicAura_NeverTicks()
    {
        AuraContainer container = Container(AuraType.ModDamageDone, 30, 0, 4000, out Aura aura);

        Assert.False(aura.Effects[0].IsPeriodic);
        Assert.Empty(container.Update(2000).Ticks);
        Assert.Single(container.Update(2000).Expired);
    }

    /// <summary>A zero diff does nothing at all, which is what an out-of-phase map hands in.</summary>
    [Fact]
    public void AZeroDiff_DoesNothing()
    {
        AuraContainer container = Container(AuraType.PeriodicDamage, 30, 1000, 4000, out Aura aura);

        (IReadOnlyList<AuraTick> ticks, IReadOnlyList<Aura> expired) = container.Update(0);

        Assert.Empty(ticks);
        Assert.Empty(expired);
        Assert.Equal(4000, aura.RemainingMs);
    }
}

/// <summary>
/// <c>AuraContainer</c>: slots, refreshing and who owns what.
/// </summary>
public sealed class AuraContainerTests
{
    private static readonly ObjectGuid Caster = new(1);
    private static readonly ObjectGuid Other = new(2);

    private static Aura? Apply(AuraContainer container, SpellEntry spell, ObjectGuid caster, int durationMs = 10000) =>
        container.Apply(spell, caster, casterLevel: 20, durationMs, AuraFixture.Flat);

    /// <summary>A spell with no aura effects applies nothing and says so.</summary>
    /// <remarks>
    /// Every cast runs through here, and most spells are not auras. Creating an empty aura for each
    /// would fill the target's slots with icons that do nothing.
    /// </remarks>
    [Fact]
    public void ASpellWithNoAuraEffects_AppliesNothing()
    {
        AuraContainer container = new();

        Assert.Null(Apply(container, AuraFixture.DirectDamageOnly(), Caster));
        Assert.Equal(0, container.Count);
    }

    /// <summary>Slots are handed out from the bottom, and a freed one is reused.</summary>
    [Fact]
    public void Slots_AreTheLowestFree()
    {
        AuraContainer container = new();

        Aura first = Apply(container, AuraFixture.Periodic(AuraType.PeriodicDamage, 10, 1000, id: 1), Caster)!;
        Aura second = Apply(container, AuraFixture.Periodic(AuraType.PeriodicDamage, 10, 1000, id: 2), Caster)!;

        Assert.Equal(0, first.Slot);
        Assert.Equal(1, second.Slot);

        container.Remove(first);

        Aura third = Apply(container, AuraFixture.Periodic(AuraType.PeriodicDamage, 10, 1000, id: 3), Caster)!;

        Assert.Equal(0, third.Slot);
    }

    /// <summary>
    /// The same caster's second cast refreshes in place, keeping its slot.
    /// </summary>
    /// <remarks>
    /// Keeping the slot is what stops the client's buff bar flickering the icon away and back on
    /// every refresh.
    /// </remarks>
    [Fact]
    public void ARecast_RefreshesInPlaceAndKeepsItsSlot()
    {
        AuraContainer container = new();
        SpellEntry spell = AuraFixture.Periodic(AuraType.PeriodicDamage, 10, 1000);

        Aura first = Apply(container, spell, Caster)!;
        container.Update(4000);

        Assert.True(first.RemainingMs < 10000);

        Aura second = Apply(container, spell, Caster)!;

        Assert.Equal(1, container.Count);
        Assert.Equal(first.Slot, second.Slot);
        Assert.Equal(10000, second.RemainingMs);
    }

    /// <summary>
    /// Two casters each get their own aura from the same spell.
    /// </summary>
    /// <remarks>
    /// The caster is part of the key precisely because of this: two warlocks each keep their own
    /// Corruption on the same target, and one refreshing must not take the other's over.
    /// </remarks>
    [Fact]
    public void TwoCasters_EachGetTheirOwn()
    {
        AuraContainer container = new();
        SpellEntry spell = AuraFixture.Periodic(AuraType.PeriodicDamage, 10, 1000);

        Aura mine = Apply(container, spell, Caster)!;
        Aura theirs = Apply(container, spell, Other)!;

        Assert.Equal(2, container.Count);
        Assert.NotEqual(mine.Slot, theirs.Slot);
        Assert.Equal(mine, container.Find(spell.Id, Caster));
        Assert.Equal(theirs, container.Find(spell.Id, Other));
    }

    /// <summary>The tick amount comes from the effect's calculated value, not from the raw column.</summary>
    [Fact]
    public void TheTickAmount_IsTheCalculatedValue()
    {
        AuraContainer container = new();

        Aura aura = Apply(container, AuraFixture.Periodic(AuraType.PeriodicDamage, 21, 3000), Caster)!;

        Assert.Equal(21, aura.Effects[0].Amount);
    }

    /// <summary>An unhandled type is still applied and still shown — it just does nothing.</summary>
    /// <remarks>
    /// A buff on the bar that does nothing is worse than one that never appeared, so this is
    /// asserted rather than left implicit until the handler arrives.
    /// <para>
    /// The example is a damage modifier because that is one of the ones still outstanding. It used
    /// to be a slow, which now works — so if this test ever needs repointing again, that is a
    /// handler arriving rather than a regression.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUnhandledType_IsStillApplied()
    {
        AuraContainer container = new();

        Aura aura = Apply(container, AuraFixture.Periodic(AuraType.ModDamageDone, 50, 0), Caster)!;

        Assert.Equal(1, container.Count);
        Assert.False(aura.Effects[0].IsHandled);
    }
}

/// <summary>
/// <c>AuraApplication::BuildUpdatePacket</c>: what goes on the wire.
/// </summary>
public sealed class AuraUpdatePacketTests
{
    private static readonly ObjectGuid Target = ObjectGuid.Create(HighGuid.Unit, 299, 42);
    private static readonly ObjectGuid Caster = ObjectGuid.Create(HighGuid.Player, 7);

    private static byte[] Write(in AuraSlotUpdate update)
    {
        PacketWriter writer = new();
        AuraUpdate.WriteApplied(writer, Target, update);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The caster guid is written only when the caster is <i>not</i> the target.
    /// </summary>
    /// <remarks>
    /// The flag is what the client branches on. Setting it on someone else's debuff drops a packed
    /// guid the client is still reading, and every field after it shifts.
    /// </remarks>
    [Fact]
    public void TheCasterGuid_IsWrittenOnlyWhenTheCasterIsNotTheTarget()
    {
        byte[] fromOther = Write(new AuraSlotUpdate(0, 133, 0x01, 20, 1, Caster));
        byte[] fromSelf = Write(new AuraSlotUpdate(0, 133, 0x01 | AuraUpdate.FlagCaster, 20, 1, Caster));

        Assert.True(fromOther.Length > fromSelf.Length);
    }

    /// <summary>The two durations follow only when the duration flag says so.</summary>
    [Fact]
    public void TheDurations_FollowOnlyWhenFlagged()
    {
        byte[] timed = Write(new AuraSlotUpdate(
            0, 133, AuraUpdate.FlagCaster | AuraUpdate.FlagDuration, 20, 1, Caster, 12000, 8000));

        byte[] permanent = Write(new AuraSlotUpdate(0, 133, AuraUpdate.FlagCaster, 20, 1, Caster));

        Assert.Equal(8, timed.Length - permanent.Length);
    }

    /// <summary>The body reads back field by field.</summary>
    [Fact]
    public void TheBody_ReadsBackFieldByField()
    {
        byte[] bytes = Write(new AuraSlotUpdate(
            Slot: 3,
            SpellId: 133,
            Flags: AuraUpdate.FlagDuration,
            CasterLevel: 20,
            StackAmount: 2,
            Caster: Caster,
            MaxDurationMs: 12000,
            RemainingMs: 8000));

        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid target));
        Assert.Equal(Target, target);

        Assert.True(reader.TryReadUInt8(out byte slot));
        Assert.Equal(3, slot);

        Assert.True(reader.TryReadUInt32(out uint spellId));
        Assert.Equal(133u, spellId);

        Assert.True(reader.TryReadUInt8(out byte flags));
        Assert.Equal(AuraUpdate.FlagDuration, flags);

        Assert.True(reader.TryReadUInt8(out byte casterLevel));
        Assert.Equal(20, casterLevel);

        Assert.True(reader.TryReadUInt8(out byte stack));
        Assert.Equal(2, stack);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid caster));
        Assert.Equal(Caster, caster);

        Assert.True(reader.TryReadUInt32(out uint maxDuration));
        Assert.Equal(12000u, maxDuration);

        Assert.True(reader.TryReadUInt32(out uint remaining));
        Assert.Equal(8000u, remaining);

        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// A zero stack is written as one.
    /// </summary>
    /// <remarks>
    /// The client treats a zero stack as an aura that is not there and draws nothing — the aura
    /// would be applied server-side and invisible.
    /// </remarks>
    [Fact]
    public void AZeroStack_IsWrittenAsOne()
    {
        byte[] bytes = Write(new AuraSlotUpdate(0, 133, AuraUpdate.FlagCaster, 20, StackAmount: 0));

        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        reader.Skip(1 + 4 + 1 + 1);

        Assert.True(reader.TryReadUInt8(out byte stack));
        Assert.Equal(1, stack);
    }

    /// <summary>
    /// A removal is the slot and a zero spell id, and ends there.
    /// </summary>
    /// <remarks>
    /// Writing the full body with a zero id instead leaves bytes the client reads as the start of
    /// something else.
    /// </remarks>
    [Fact]
    public void ARemoval_EndsAtTheZeroSpellId()
    {
        PacketWriter writer = new();
        AuraUpdate.WriteRemoved(writer, Target, slot: 4);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid target));
        Assert.Equal(Target, target);

        Assert.True(reader.TryReadUInt8(out byte slot));
        Assert.Equal(4, slot);

        Assert.True(reader.TryReadUInt32(out uint spellId));
        Assert.Equal(0u, spellId);

        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>The periodic log's trailer differs by type, so a heal is shorter than a damage tick.</summary>
    [Fact]
    public void ThePeriodicLog_HasADifferentTrailerPerType()
    {
        static byte[] Log(uint auraType)
        {
            PacketWriter writer = new();
            AuraUpdate.WritePeriodicLog(writer, Target, Caster, 133, auraType, 120, 0, 4);

            return writer.WrittenSpan.ToArray();
        }

        // Damage writes five words and a byte; a heal writes three and a byte.
        Assert.Equal(8, Log(AuraType.PeriodicDamage).Length - Log(AuraType.PeriodicHeal).Length);
    }

    /// <summary>A damage tick's body reads back field by field.</summary>
    [Fact]
    public void ADamageTick_ReadsBackFieldByField()
    {
        PacketWriter writer = new();

        AuraUpdate.WritePeriodicLog(
            writer, Target, Caster, spellId: 172, auraType: AuraType.PeriodicDamage,
            amount: 120, overflow: 20, schoolMask: 32);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid target));
        Assert.Equal(Target, target);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid caster));
        Assert.Equal(Caster, caster);

        Assert.True(reader.TryReadUInt32(out uint spellId));
        Assert.Equal(172u, spellId);

        Assert.True(reader.TryReadUInt32(out uint count));
        Assert.Equal(1u, count);

        Assert.True(reader.TryReadUInt32(out uint auraType));
        Assert.Equal(AuraType.PeriodicDamage, auraType);

        Assert.True(reader.TryReadUInt32(out uint amount));
        Assert.Equal(120u, amount);

        Assert.True(reader.TryReadUInt32(out uint overkill));
        Assert.Equal(20u, overkill);

        Assert.True(reader.TryReadUInt32(out uint school));
        Assert.Equal(32u, school);

        // Absorbed, resisted, then the critical byte that arrived in 3.1.2.
        Assert.Equal(4 + 4 + 1, reader.Remaining);
    }
}

/// <summary>Auras landing, ticking and expiring through a real map.</summary>
public sealed class MapAuraTests(ITestOutputHelper output)
{
    /// <summary>A map that knows about spells, so an aura can resolve its own duration.</summary>
    private static (Map Map, Player Caster, Creature Target, MapCombatFixture.Link Link) Engaged(SpellStores stores)
    {
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) =
            MapCombatFixture.Engaged(spells: stores);

        // Otherwise the swing loop kills the target before the aura has ticked twice, and every
        // assertion below is about a corpse.
        caster.AttackStop();

        target.MaxHealth = 100000;
        target.Health = 100000;

        return (map, caster, target, link);
    }

    /// <summary>
    /// A cast puts its aura on the target and tells the client.
    /// </summary>
    /// <remarks>
    /// Immolate: direct damage from effect 0 and a burn from effect 1, which is exactly the case
    /// that was silently dropping half of every damage-over-time spell in the game.
    /// </remarks>
    [RequiresClientDataFact]
    public void ACast_AppliesItsAura()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = Engaged(stores);

        // Immolate rank 1.
        Assert.True(stores.Spells.TryGet(348, out SpellEntry immolate));

        map.CompleteCast(caster, immolate, target, castCount: 1);

        Assert.True(target.Auras.Has(348), "the aura did not land");
        Assert.Contains(link.AurasApplied, applied => applied.SpellId == 348);

        Aura aura = target.Auras.Find(348, caster.Guid)!;

        Assert.Equal(stores.DurationMs(immolate), aura.MaxDurationMs);
        Assert.Contains(aura.Effects, effect => effect.Type == AuraType.PeriodicDamage);

        output.WriteLine(
            $"Immolate: {aura.MaxDurationMs} ms, {aura.Effects[0].Amount} per {aura.Effects[0].AmplitudeMs} ms");
    }

    /// <summary>A spell with no aura effects leaves the target clean.</summary>
    /// <remarks>
    /// Lesser Heal rather than Fireball: Fireball rank 1 <i>does</i> carry an aura — a one-point
    /// burn on effect 1 — which is easy to assume away and is exactly the thing this task was for.
    /// </remarks>
    [RequiresClientDataFact]
    public void ADirectSpell_AppliesNoAura()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, _, MapCombatFixture.Link link) = Engaged(stores);

        // Lesser Heal rank 1.
        Assert.True(stores.Spells.TryGet(2050, out SpellEntry heal));
        Assert.DoesNotContain(heal.UsedEffects, effect => effect.Effect == SpellEffectId.ApplyAura);

        caster.Health = 1;

        map.CompleteCast(caster, heal, caster, castCount: 1);

        Assert.Equal(0, caster.Auras.Count);
        Assert.Empty(link.AurasApplied);
    }

    /// <summary>
    /// Fireball carries a burn, and it lands alongside the direct damage.
    /// </summary>
    /// <remarks>
    /// The case that named this task: effect 0 is the impact and effect 1 is a two-second burn. The
    /// direct damage was landing and the burn was silently dropped.
    /// </remarks>
    [RequiresClientDataFact]
    public void Fireball_LandsItsDamageAndItsBurn()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = Engaged(stores);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        map.CompleteCast(caster, fireball, target, castCount: 1);

        Assert.NotEmpty(link.SpellDamage);
        Assert.True(target.Auras.Has(133), "Fireball's burn did not land");

        uint afterTheImpact = target.Health;
        uint amplitude = target.Auras.Find(133, caster.Guid)!.Effects[0].AmplitudeMs;

        map.Update(gameplayDiff: amplitude, sessionDiff: amplitude);

        Assert.True(target.Health < afterTheImpact, "the burn did not tick");
    }

    /// <summary>A burn takes health on its own schedule, after the cast is over.</summary>
    [RequiresClientDataFact]
    public void ABurn_TicksAfterTheCast()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = Engaged(stores);

        Assert.True(stores.Spells.TryGet(348, out SpellEntry immolate));

        map.CompleteCast(caster, immolate, target, castCount: 1);

        uint afterTheCast = target.Health;

        // One amplitude, so exactly one tick comes due.
        Aura aura = target.Auras.Find(348, caster.Guid)!;
        uint amplitude = aura.Effects[0].AmplitudeMs;

        map.Update(gameplayDiff: amplitude, sessionDiff: amplitude);

        Assert.True(target.Health < afterTheCast, "the burn did not tick");
        Assert.Single(link.AuraTicks);
        Assert.Equal(afterTheCast - target.Health, link.AuraTicks[0].Amount);
        Assert.Equal(AuraType.PeriodicDamage, link.AuraTicks[0].AuraType);
    }

    /// <summary>
    /// A tick keeps its caster on the threat list.
    /// </summary>
    /// <remarks>
    /// Without it a target burning from one player and hit once by another walks off after the
    /// second — the burn would be doing all the damage and generating none of the threat.
    /// </remarks>
    [RequiresClientDataFact]
    public void ATick_GeneratesThreat()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, _) = Engaged(stores);

        Assert.True(stores.Spells.TryGet(348, out SpellEntry immolate));

        map.CompleteCast(caster, immolate, target, castCount: 1);

        float afterTheCast = target.Threat.GetThreat(caster);
        uint amplitude = target.Auras.Find(348, caster.Guid)!.Effects[0].AmplitudeMs;

        map.Update(gameplayDiff: amplitude, sessionDiff: amplitude);

        Assert.True(target.Threat.GetThreat(caster) > afterTheCast, "the tick generated no threat");
    }

    /// <summary>An aura that runs out is taken off and the client is told.</summary>
    [RequiresClientDataFact]
    public void AnExpiredAura_IsRemovedAndBroadcast()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = Engaged(stores);

        Assert.True(stores.Spells.TryGet(348, out SpellEntry immolate));

        map.CompleteCast(caster, immolate, target, castCount: 1);

        uint duration = (uint)stores.DurationMs(immolate);

        map.Update(gameplayDiff: duration, sessionDiff: duration);

        Assert.Equal(0, target.Auras.Count);
        Assert.Contains(link.AurasRemoved, removed => removed.Target == target.Guid);
    }

    /// <summary>
    /// A burn can finish a target off, and the kill is noticed the same way a swing's is.
    /// </summary>
    /// <remarks>
    /// This is the case the whole feature exists for: damage that lands with nobody swinging. If the
    /// tick took health without going through the death path the creature would sit at zero health,
    /// alive, and never pay out.
    /// </remarks>
    [RequiresClientDataFact]
    public void ABurn_CanKill()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, _) = Engaged(stores);

        Assert.True(stores.Spells.TryGet(348, out SpellEntry immolate));

        map.CompleteCast(caster, immolate, target, castCount: 1);

        target.Health = 1;

        uint amplitude = target.Auras.Find(348, caster.Guid)!.Effects[0].AmplitudeMs;

        map.Update(gameplayDiff: amplitude, sessionDiff: amplitude);

        Assert.Equal(0u, target.Health);
        Assert.Equal(DeathState.Corpse, target.DeathState);
    }

    /// <summary>Death takes every aura with it, and the client is told about each.</summary>
    [RequiresClientDataFact]
    public void Death_ClearsTheAuras()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = Engaged(stores);

        Assert.True(stores.Spells.TryGet(348, out SpellEntry immolate));

        map.CompleteCast(caster, immolate, target, castCount: 1);

        Assert.Equal(1, target.Auras.Count);

        target.Health = 1;

        map.CompleteCast(caster, immolate, target, castCount: 2);

        Assert.Equal(DeathState.Corpse, target.DeathState);
        Assert.Equal(0, target.Auras.Count);
        Assert.Contains(link.AurasRemoved, removed => removed.Target == target.Guid);
    }

    /// <summary>A cast at a corpse applies nothing, rather than decorating it.</summary>
    [RequiresClientDataFact]
    public void ACastAtACorpse_AppliesNoAura()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, _) = Engaged(stores);

        Assert.True(stores.Spells.TryGet(348, out SpellEntry immolate));

        target.Kill();

        map.CompleteCast(caster, immolate, target, castCount: 1);

        Assert.Equal(0, target.Auras.Count);
    }

    /// <summary>
    /// A heal-over-time cannot take its target above its maximum, and reports the waste.
    /// </summary>
    /// <remarks>
    /// Health is unsigned and the field goes straight to the client, so an overheal would draw a bar
    /// past its own end.
    /// </remarks>
    [RequiresClientDataFact]
    public void AHealOverTime_CannotOverfill()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, _, MapCombatFixture.Link link) = Engaged(stores);

        // Renew rank 1, on the caster itself.
        Assert.True(stores.Spells.TryGet(139, out SpellEntry renew));

        caster.MaxHealth = 100;
        caster.Health = 99;

        map.CompleteCast(caster, renew, caster, castCount: 1);

        uint amplitude = caster.Auras.Find(139, caster.Guid)!.Effects[0].AmplitudeMs;

        map.Update(gameplayDiff: amplitude, sessionDiff: amplitude);

        Assert.Equal(100u, caster.Health);

        (_, _, uint auraType, uint amount, uint overflow) = link.AuraTicks[0];

        Assert.Equal(AuraType.PeriodicHeal, auraType);
        Assert.Equal(1u, amount);
        Assert.True(overflow > 0, "the wasted healing was not reported");
    }

    /// <summary>
    /// An aura cast on yourself says so in its flags, which is what keeps the caster guid off the wire.
    /// </summary>
    [RequiresClientDataFact]
    public void ASelfCastAura_SetsTheCasterFlag()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = Engaged(stores);

        Assert.True(stores.Spells.TryGet(139, out SpellEntry renew));
        Assert.True(stores.Spells.TryGet(348, out SpellEntry immolate));

        map.CompleteCast(caster, renew, caster, castCount: 1);
        map.CompleteCast(caster, immolate, target, castCount: 2);

        byte onSelf = link.AurasApplied.Find(a => a.SpellId == 139).Flags;
        byte onOther = link.AurasApplied.Find(a => a.SpellId == 348).Flags;

        Assert.Equal(AuraUpdate.FlagCaster, onSelf & AuraUpdate.FlagCaster);
        Assert.Equal(0, onOther & AuraUpdate.FlagCaster);
    }
}
