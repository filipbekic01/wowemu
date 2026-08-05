using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using WowEmu.Protocol;

// The test namespace ends in `Unit`, which shadows the class of the same name. Verified required:
// removing this alias is a compile error, not a style nit, however the IDE greys it out.
using GameUnit = WowEmu.Game.Unit;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The wire format of a cast: <c>CMSG_CAST_SPELL</c> in, <c>SMSG_SPELL_START</c>/<c>_GO</c> out.
/// </summary>
/// <remarks>
/// Read back field by field. The target block's layout is decided entirely by its first four bytes,
/// so what matters is which blocks are present and in what order.
/// </remarks>
public sealed class SpellCastPacketTests
{
    private static readonly ObjectGuid Caster = ObjectGuid.Create(HighGuid.Player, 7);
    private static readonly ObjectGuid Target = ObjectGuid.Create(HighGuid.Unit, 299, 42);

    private static byte[] Written(Action<PacketWriter> write)
    {
        PacketWriter writer = new();
        write(writer);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>A cast at a unit round-trips through the reader.</summary>
    [Fact]
    public void ACastAtAUnit_RoundTrips()
    {
        byte[] bytes = Written(writer =>
        {
            writer.WriteUInt8(3);          // cast count
            writer.WriteUInt32(133);       // Fireball
            writer.WriteUInt8(0);          // cast flags
            SpellCast.WriteTargets(writer, new SpellCastTargets(SpellCastTargetFlags.Unit, ObjectTarget: Target));
        });

        PacketReader reader = new(bytes);

        Assert.True(SpellCast.TryRead(ref reader, out byte castCount, out uint spellId, out SpellCastTargets targets));

        Assert.Equal(3, castCount);
        Assert.Equal(133u, spellId);
        Assert.Equal(SpellCastTargetFlags.Unit, targets.Mask);
        Assert.Equal(Target, targets.ObjectTarget);
        Assert.True(targets.HasObjectTarget);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>A self-cast has an empty mask and nothing after it.</summary>
    /// <remarks>
    /// The reader returns early on a zero mask. Reading on regardless would consume bytes belonging
    /// to whatever follows in the packet.
    /// </remarks>
    [Fact]
    public void ASelfCast_HasNothingAfterTheMask()
    {
        byte[] bytes = Written(writer =>
        {
            writer.WriteUInt8(1);
            writer.WriteUInt32(1459);
            writer.WriteUInt8(0);
            writer.WriteUInt32(0);   // TARGET_FLAG_NONE
        });

        PacketReader reader = new(bytes);

        Assert.True(SpellCast.TryRead(ref reader, out _, out _, out SpellCastTargets targets));

        Assert.Equal(SpellCastTargetFlags.None, targets.Mask);
        Assert.False(targets.HasObjectTarget);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// Five flags share one guid field, so only one guid is read however many are set.
    /// </summary>
    /// <remarks>
    /// Reading a guid per flag consumes bytes that were never sent and desynchronises everything
    /// after — including, on a longer packet, the destination coordinates.
    /// </remarks>
    [Fact]
    public void TheObjectFlags_ShareOneGuid()
    {
        SpellCastTargets sent = new(
            SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseEnemy, ObjectTarget: Target);

        byte[] bytes = Written(writer => SpellCast.WriteTargets(writer, sent));

        PacketReader reader = new(bytes);

        Assert.True(SpellCast.TryReadTargets(ref reader, out SpellCastTargets read));

        Assert.Equal(Target, read.ObjectTarget);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>A ground-targeted cast carries a transport guid and three floats.</summary>
    [Fact]
    public void AGroundTargetedCast_CarriesAPoint()
    {
        SpellCastTargets sent = new(
            SpellCastTargetFlags.DestLocation,
            Destination: new Position(-8913.5f, 554.6f, 93.7f, 0f));

        byte[] bytes = Written(writer => SpellCast.WriteTargets(writer, sent));

        PacketReader reader = new(bytes);

        Assert.True(SpellCast.TryReadTargets(ref reader, out SpellCastTargets read));

        Assert.True(read.HasDestination);
        Assert.Equal(-8913.5f, read.Destination.X, 0.01f);
        Assert.Equal(554.6f, read.Destination.Y, 0.01f);
        Assert.Equal(93.7f, read.Destination.Z, 0.01f);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>A truncated packet is refused rather than read as zeros.</summary>
    [Fact]
    public void ATruncatedCast_IsRefused()
    {
        PacketReader reader = new([1, 2, 3]);

        Assert.False(SpellCast.TryRead(ref reader, out _, out _, out _));
    }

    /// <summary>A mask promising a guid that is not there is refused.</summary>
    [Fact]
    public void AMaskPromisingMoreThanIsThere_IsRefused()
    {
        byte[] bytes = Written(writer => writer.WriteUInt32((uint)SpellCastTargetFlags.Unit));

        PacketReader reader = new(bytes);

        Assert.False(SpellCast.TryReadTargets(ref reader, out _));
    }

    // ------------------------------------------------------------------ outgoing

    /// <summary>
    /// <c>SMSG_SPELL_START</c> writes the caster guid twice.
    /// </summary>
    /// <remarks>
    /// The first is whatever produced the cast — an item, if one was used — and the second is always
    /// the unit. Writing it once shifts every field after it by a guid, and the client reads the
    /// spell id out of the middle of a number.
    /// </remarks>
    [Fact]
    public void SpellStart_WritesTheCasterTwice()
    {
        byte[] bytes = Written(writer => SpellCast.WriteSpellStart(
            writer, Caster, castCount: 4, spellId: 133,
            SpellCastFlags.HasTrajectory, castTimeMs: 1500,
            new SpellCastTargets(SpellCastTargetFlags.Unit, ObjectTarget: Target)));

        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid first));
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid second));

        Assert.Equal(Caster, first);
        Assert.Equal(Caster, second);

        Assert.True(reader.TryReadUInt8(out byte castCount));
        Assert.Equal(4, castCount);

        Assert.True(reader.TryReadUInt32(out uint spellId));
        Assert.Equal(133u, spellId);

        Assert.True(reader.TryReadUInt32(out uint flags));
        Assert.Equal((uint)SpellCastFlags.HasTrajectory, flags);

        Assert.True(reader.TryReadUInt32(out uint castTime));
        Assert.Equal(1500u, castTime);
    }

    /// <summary>The remaining-power field follows its flag, and is absent without it.</summary>
    [Fact]
    public void ThePowerField_FollowsItsFlag()
    {
        byte[] without = Written(writer => SpellCast.WriteSpellStart(
            writer, Caster, 1, 133, SpellCastFlags.None, 0, SpellCastTargets.Self, powerLeft: 900));

        byte[] with = Written(writer => SpellCast.WriteSpellStart(
            writer, Caster, 1, 133, SpellCastFlags.PowerLeftSelf, 0, SpellCastTargets.Self, powerLeft: 900));

        Assert.Equal(without.Length + 4, with.Length);
        Assert.Equal(900u, BitConverter.ToUInt32(with, with.Length - 4));
    }

    /// <summary>
    /// <c>SMSG_SPELL_GO</c>'s hit and miss lists use full guids, not packed ones.
    /// </summary>
    /// <remarks>
    /// The one place in this packet that does not pack, and the two lists have different strides —
    /// a miss carries a reason byte after its guid and a hit does not.
    /// </remarks>
    [Fact]
    public void SpellGo_UsesFullGuidsInItsTargetLists()
    {
        ObjectGuid missed = ObjectGuid.Create(HighGuid.Unit, 299, 43);

        byte[] bytes = Written(writer => SpellCast.WriteSpellGo(
            writer, Caster, castCount: 2, spellId: 133,
            SpellCastFlags.Unknown9, timestampMs: 12345,
            hits: [Target],
            misses: [(missed, 1)],
            new SpellCastTargets(SpellCastTargetFlags.Unit, ObjectTarget: Target)));

        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        reader.Skip(1 + 4 + 4 + 4);   // cast count, spell id, flags, timestamp

        Assert.True(reader.TryReadUInt8(out byte hitCount));
        Assert.Equal(1, hitCount);

        Assert.True(reader.TryReadUInt64(out ulong hit));
        Assert.Equal(Target.Value, hit);

        Assert.True(reader.TryReadUInt8(out byte missCount));
        Assert.Equal(1, missCount);

        Assert.True(reader.TryReadUInt64(out ulong miss));
        Assert.Equal(missed.Value, miss);

        Assert.True(reader.TryReadUInt8(out byte reason));
        Assert.Equal(1, reason);
    }

    /// <summary>A ground-targeted cast ends with one extra byte.</summary>
    /// <remarks>
    /// Upstream sends it whenever the destination flag is set and never explains it. Omitting it
    /// truncates the packet.
    /// </remarks>
    [Fact]
    public void AGroundTargetedSpellGo_EndsWithAnExtraByte()
    {
        SpellCastTargets ground = new(
            SpellCastTargetFlags.DestLocation, Destination: new Position(1f, 2f, 3f, 0f));

        byte[] withGround = Written(writer => SpellCast.WriteSpellGo(
            writer, Caster, 1, 133, SpellCastFlags.None, 0, [Target], [], ground));

        // Measured against the same packet without the trailing byte, rather than against a
        // hand-computed length: packed guids vary in width, so arithmetic here would be asserting
        // the encoding rather than the trailer.
        byte[] justTheTargets = Written(writer => SpellCast.WriteTargets(writer, ground));

        byte[] noDestination = Written(writer => SpellCast.WriteSpellGo(
            writer, Caster, 1, 133, SpellCastFlags.None, 0, [Target], [], SpellCastTargets.Self));

        byte[] selfTargets = Written(writer => SpellCast.WriteTargets(writer, SpellCastTargets.Self));

        int groundOverhead = withGround.Length - justTheTargets.Length;
        int selfOverhead = noDestination.Length - selfTargets.Length;

        // Identical but for the trailing byte the destination flag adds.
        Assert.Equal(selfOverhead + 1, groundOverhead);
        Assert.Equal(0, withGround[^1]);
    }

    [Fact]
    public void CastFailed_QuotesTheCastCountAndSpell()
    {
        byte[] bytes = Written(writer =>
            SpellCast.WriteCastFailed(writer, castCount: 9, spellId: 133, SpellCastResult.OutOfRange));

        Assert.Equal(6, bytes.Length);
        Assert.Equal(9, bytes[0]);
        Assert.Equal(133u, BitConverter.ToUInt32(bytes, 1));
        Assert.Equal((byte)SpellCastResult.OutOfRange, bytes[5]);
    }

    /// <summary>
    /// The success sentinel is 255 and is never written.
    /// </summary>
    /// <remarks>
    /// Upstream's own value, outside the client's error range. Writing it would have the client look
    /// up a string that does not exist.
    /// </remarks>
    [Fact]
    public void TheSuccessSentinel_IsOutsideTheClientsRange()
    {
        Assert.Equal(255, (byte)SpellCastResult.Ok);
        Assert.True((byte)SpellCastResult.UnitNotInFront < 255);
    }
}

/// <summary>Cast timers, cooldowns and the global cooldown.</summary>
public sealed class SpellCastStateTests
{
    private static SpellEntry Spell(
        uint id = 1, uint recoveryTime = 0, uint startRecoveryTime = 1500,
        uint manaCost = 0, uint manaCostPercentage = 0, uint powerType = 0) =>
        new(id, "test", "", new uint[SpellEntry.AttributeWords],
            CastingTimeIndex: 0, RecoveryTime: recoveryTime, CategoryRecoveryTime: 0,
            StartRecoveryCategory: 0, StartRecoveryTime: startRecoveryTime, InterruptFlags: 0,
            Targets: 0, PowerType: powerType, ManaCost: manaCost, ManaCostPerLevel: 0,
            ManaCostPercentage: manaCostPercentage, RangeIndex: 0, Speed: 0f, DurationIndex: 0,
            BaseLevel: 0, SpellLevel: 0, MaxLevel: 0, SchoolMask: 0, DmgClass: 0, PreventionType: 0,
            SpellFamilyName: 0, MaxAffectedTargets: 0, SpellIconId: 0, SpellVisual: 0,
            Effects: new SpellEffectEntry[SpellConstants.MaxEffects]);

    [Fact]
    public void ANewState_IsIdleAndReady()
    {
        SpellCastState state = new();

        Assert.Equal(CastState.Idle, state.State);
        Assert.True(state.IsGlobalCooldownReady);
        Assert.True(state.IsReady(133));
    }

    [Fact]
    public void ACastBar_RunsDownAndFinishes()
    {
        SpellCastState state = new();

        state.Begin(Spell(), target: null, castCount: 1, castTimeMs: 1500);

        Assert.Equal(CastState.Casting, state.State);

        Assert.Null(state.Update(1000));
        Assert.Equal(CastState.Casting, state.State);

        PendingCast? finished = state.Update(500);

        Assert.NotNull(finished);
        Assert.Equal(CastState.Idle, state.State);
        Assert.Equal(1, finished!.Value.CastCount);
    }

    /// <summary>A finished cast is reported once, not on every tick after it.</summary>
    [Fact]
    public void AFinishedCast_IsReportedOnce()
    {
        SpellCastState state = new();

        state.Begin(Spell(), null, 1, 500);

        Assert.NotNull(state.Update(500));
        Assert.Null(state.Update(500));
        Assert.Null(state.Update(500));
    }

    [Fact]
    public void Cancelling_EndsTheCastAndReturnsIt()
    {
        SpellCastState state = new();

        state.Begin(Spell(id: 133), null, 2, 1500);

        PendingCast? cancelled = state.Cancel();

        Assert.NotNull(cancelled);
        Assert.Equal(133u, cancelled!.Value.Spell.Id);
        Assert.Equal(CastState.Idle, state.State);
        Assert.Null(state.Cancel());
    }

    /// <summary>
    /// A spell with no recovery time starts no global cooldown.
    /// </summary>
    /// <remarks>
    /// Substituting the default for zero is the obvious simplification and it puts every instant
    /// rage and energy ability behind a wait it should not have.
    /// </remarks>
    [Fact]
    public void ASpellWithNoRecoveryTime_StartsNoGlobalCooldown()
    {
        SpellCastState state = new();

        state.StartGlobalCooldown(Spell(startRecoveryTime: 0));

        Assert.True(state.IsGlobalCooldownReady);

        state.StartGlobalCooldown(Spell(startRecoveryTime: 1500));

        Assert.False(state.IsGlobalCooldownReady);
        Assert.Equal(1500, state.GlobalCooldownMs);
    }

    [Fact]
    public void TheGlobalCooldown_RunsDown()
    {
        SpellCastState state = new();

        state.StartGlobalCooldown(Spell(startRecoveryTime: 1500));

        state.Update(1000);
        Assert.False(state.IsGlobalCooldownReady);

        state.Update(500);
        Assert.True(state.IsGlobalCooldownReady);
        Assert.Equal(0, state.GlobalCooldownMs);
    }

    /// <summary>Cooldowns tick whether or not anything is being cast.</summary>
    [Fact]
    public void Cooldowns_TickWithoutACast()
    {
        SpellCastState state = new();

        state.StartCooldown(133, 3000);

        Assert.False(state.IsReady(133));

        state.Update(3000);

        Assert.True(state.IsReady(133));
        Assert.Equal(0, state.CooldownMs(133));
    }

    [Fact]
    public void EachSpell_HasItsOwnCooldown()
    {
        SpellCastState state = new();

        state.StartCooldown(133, 3000);
        state.StartCooldown(116, 1000);

        state.Update(1000);

        Assert.False(state.IsReady(133));
        Assert.True(state.IsReady(116));
    }

    /// <summary>A zero diff advances nothing, on a cast bar as much as anywhere.</summary>
    [Fact]
    public void AZeroDiff_AdvancesNothing()
    {
        SpellCastState state = new();

        state.Begin(Spell(), null, 1, 1500);
        state.StartGlobalCooldown(Spell(startRecoveryTime: 1500));
        state.StartCooldown(133, 3000);

        for (int i = 0; i < 100; i++)
        {
            Assert.Null(state.Update(0));
        }

        Assert.Equal(1500, state.GlobalCooldownMs);
        Assert.Equal(3000, state.CooldownMs(133));
        Assert.Equal(CastState.Casting, state.State);
    }
}

/// <summary>Whether a cast is allowed, and which refusal the client is told.</summary>
public sealed class SpellCastCheckTests
{
    private static SpellStores Stores() => SpellStores.Load(ClientData.DbcDirectory);

    private static (Player Caster, Creature Target) Pair(float distance)
    {
        (Map _, Player caster, Creature target, MapCombatFixture.Link _) = MapCombatFixture.Engaged(distance);

        // The fixture starts the player already swinging; casting is a separate question.
        caster.AttackStop();

        return (caster, target);
    }

    private static bool CanSee() => true;

    [RequiresClientDataFact]
    public void ASpellInRangeAtALiveTarget_IsAllowed()
    {
        SpellStores stores = Stores();
        (Player caster, Creature target) = Pair(distance: 10f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.BaseMana = 1000;
        caster.SetMaxPower(GameUnit.PowerMana, 1000);
        caster.SetPower(GameUnit.PowerMana, 1000);

        Assert.Equal(
            SpellCastResult.Ok,
            SpellCastChecks.Check(caster, fireball, target, stores, caster.Casting, CanSee));
    }

    [RequiresClientDataFact]
    public void ATargetTooFarAway_IsOutOfRange()
    {
        SpellStores stores = Stores();
        (Player caster, Creature target) = Pair(distance: 500f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.BaseMana = 1000;
        caster.SetMaxPower(GameUnit.PowerMana, 1000);
        caster.SetPower(GameUnit.PowerMana, 1000);

        Assert.Equal(
            SpellCastResult.OutOfRange,
            SpellCastChecks.Check(caster, fireball, target, stores, caster.Casting, CanSee));
    }

    /// <summary>
    /// A dead target is reported as dead, not as out of range.
    /// </summary>
    /// <remarks>
    /// The client shows only the first failure, so the order of these checks is what the player
    /// actually reads. Checking range first tells someone their target is too far away when the real
    /// problem is that it is a corpse.
    /// </remarks>
    [RequiresClientDataFact]
    public void ADeadTarget_IsReportedAsDeadRatherThanOutOfRange()
    {
        SpellStores stores = Stores();
        (Player caster, Creature target) = Pair(distance: 500f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.BaseMana = 1000;
        caster.SetMaxPower(GameUnit.PowerMana, 1000);
        caster.SetPower(GameUnit.PowerMana, 1000);
        target.DeathState = DeathState.Corpse;

        Assert.Equal(
            SpellCastResult.TargetsDead,
            SpellCastChecks.Check(caster, fireball, target, stores, caster.Casting, CanSee));
    }

    [RequiresClientDataFact]
    public void ATargetBehindAWall_IsOutOfLineOfSight()
    {
        SpellStores stores = Stores();
        (Player caster, Creature target) = Pair(distance: 10f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.BaseMana = 1000;
        caster.SetMaxPower(GameUnit.PowerMana, 1000);
        caster.SetPower(GameUnit.PowerMana, 1000);

        Assert.Equal(
            SpellCastResult.LineOfSight,
            SpellCastChecks.Check(caster, fireball, target, stores, caster.Casting, () => false));
    }

    [RequiresClientDataFact]
    public void NotEnoughPower_IsRefused()
    {
        SpellStores stores = Stores();
        (Player caster, Creature target) = Pair(distance: 10f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        // Fireball is 8 % of base mana, so an empty mana bar cannot pay for it — and the mana slot
        // is what matters even though this warrior's displayed bar is rage.
        caster.BaseMana = 1000;
        caster.SetMaxPower(GameUnit.PowerMana, 1000);
        caster.SetPower(GameUnit.PowerMana, 0);

        Assert.Equal(
            SpellCastResult.NoPower,
            SpellCastChecks.Check(caster, fireball, target, stores, caster.Casting, CanSee));
    }

    [RequiresClientDataFact]
    public void ACastAlreadyInProgress_BlocksAnother()
    {
        SpellStores stores = Stores();
        (Player caster, Creature target) = Pair(distance: 10f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.BaseMana = 1000;
        caster.SetMaxPower(GameUnit.PowerMana, 1000);
        caster.SetPower(GameUnit.PowerMana, 1000);
        caster.Casting.Begin(fireball, target, 1, 1500);

        Assert.Equal(
            SpellCastResult.SpellInProgress,
            SpellCastChecks.Check(caster, fireball, target, stores, caster.Casting, CanSee));
    }

    /// <summary>
    /// The global cooldown blocks only spells that would start one themselves.
    /// </summary>
    /// <remarks>
    /// What lets an ability with no <c>StartRecoveryTime</c> be used during it. Blocking everything
    /// unconditionally is the simpler reading and makes those abilities feel unresponsive.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheGlobalCooldown_BlocksOnlySpellsThatStartOne()
    {
        SpellStores stores = Stores();
        (Player caster, Creature target) = Pair(distance: 3f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));
        Assert.True(stores.Spells.TryGet(78, out SpellEntry heroicStrike));

        caster.BaseMana = 1000;
        caster.SetMaxPower(GameUnit.PowerMana, 1000);
        caster.SetPower(GameUnit.PowerMana, 1000);
        caster.Casting.StartGlobalCooldown(fireball);

        Assert.False(caster.Casting.IsGlobalCooldownReady);

        // Fireball starts a global cooldown, so it waits.
        Assert.Equal(
            SpellCastResult.NotReady,
            SpellCastChecks.Check(caster, fireball, target, stores, caster.Casting, CanSee));

        // Heroic Strike does not, so it does not.
        Assert.Equal(0u, heroicStrike.StartRecoveryTime);
        Assert.NotEqual(
            SpellCastResult.NotReady,
            SpellCastChecks.Check(caster, heroicStrike, target, stores, caster.Casting, CanSee));
    }

    [RequiresClientDataFact]
    public void ASpellOnItsOwnCooldown_IsNotReady()
    {
        SpellStores stores = Stores();
        (Player caster, Creature target) = Pair(distance: 10f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.BaseMana = 1000;
        caster.SetMaxPower(GameUnit.PowerMana, 1000);
        caster.SetPower(GameUnit.PowerMana, 1000);
        caster.Casting.StartCooldown(133, 5000);

        Assert.Equal(
            SpellCastResult.NotReady,
            SpellCastChecks.Check(caster, fireball, target, stores, caster.Casting, CanSee));
    }

    /// <summary>
    /// A percentage cost is taken from base mana, not from the current maximum.
    /// </summary>
    /// <remarks>
    /// Taking it from the maximum makes every percentage-priced spell get more expensive as a
    /// character gears up, which is backwards.
    /// </remarks>
    [RequiresClientDataFact]
    public void APercentageCost_IsAPercentageOfBaseMana()
    {
        SpellStores stores = Stores();
        (Player caster, _) = Pair(distance: 10f);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.BaseMana = 1000;

        // 8 % of *base* mana, which is the character sheet's figure and not the current maximum.
        Assert.Equal(80u, SpellCastChecks.PowerCost(caster, fireball));
    }

    /// <summary>A flat cost is taken as it stands.</summary>
    [RequiresClientDataFact]
    public void AFlatCost_IsTakenAsItStands()
    {
        SpellStores stores = Stores();
        (Player caster, _) = Pair(distance: 10f);

        Assert.True(stores.Spells.TryGet(2098, out SpellEntry eviscerate));

        Assert.Equal(35u, SpellCastChecks.PowerCost(caster, eviscerate));
    }
}

/// <summary>Casting driven through a real map, which is what completes a cast bar.</summary>
public sealed class MapCastingTests
{
    /// <summary>A cast bar runs down over ticks and then completes.</summary>
    [RequiresClientDataFact]
    public void ACastBar_CompletesOnTheTick()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        caster.AttackStop();

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.Casting.Begin(fireball, target, castCount: 1, castTimeMs: 1500);

        // Short of the bar.
        for (int i = 0; i < 14; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Equal(CastState.Casting, caster.Casting.State);
        Assert.Empty(link.Casts);

        map.Update(gameplayDiff: 100, sessionDiff: 100);

        Assert.Equal(CastState.Idle, caster.Casting.State);
        Assert.Contains(link.Casts, cast => cast.SpellId == 133 && cast.Landed);
    }

    /// <summary>
    /// A cast whose target died mid-bar still completes.
    /// </summary>
    /// <remarks>
    /// The bar has to close either way — the client is drawing it. What changes is that the cast
    /// lands on nothing rather than on a corpse.
    /// </remarks>
    [RequiresClientDataFact]
    public void ACastWhoseTargetDied_StillCompletes()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        caster.AttackStop();

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        caster.Casting.Begin(fireball, target, 1, 500);
        target.Kill();

        map.Update(gameplayDiff: 500, sessionDiff: 500);

        Assert.Equal(CastState.Idle, caster.Casting.State);
        Assert.Contains(link.Casts, cast => cast.Landed);
    }

    /// <summary>Completing a cast puts it on its own cooldown.</summary>
    [RequiresClientDataFact]
    public void CompletingACast_StartsItsCooldown()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, _) = MapCombatFixture.Engaged();

        caster.AttackStop();

        // Something with a real cooldown on it.
        SpellEntry withCooldown = stores.Spells.Entries.First(spell => spell.RecoveryTime > 0);

        map.CompleteCast(caster, withCooldown, target, castCount: 1);

        Assert.False(caster.Casting.IsReady(withCooldown.Id));
        Assert.Equal((int)withCooldown.RecoveryTime, caster.Casting.CooldownMs(withCooldown.Id));
    }
}

/// <summary>
/// The seven power slots, which a unit carries all at once.
/// </summary>
/// <remarks>
/// The client reads each resource from its own field and draws the one named by the unit's power
/// type. Writing everything into slot 0 shows a warrior with a full mana bar and no rage.
/// </remarks>
public sealed class PowerSlotTests
{
    [Fact]
    public void EachPowerType_HasItsOwnSlot()
    {
        Creature creature = CreatureFixture.Build();

        creature.SetPower(GameUnit.PowerMana, 100);
        creature.SetPower(GameUnit.PowerRage, 500);
        creature.SetPower(GameUnit.PowerEnergy, 75);

        Assert.Equal(100u, creature.GetPower(GameUnit.PowerMana));
        Assert.Equal(500u, creature.GetPower(GameUnit.PowerRage));
        Assert.Equal(75u, creature.GetPower(GameUnit.PowerEnergy));
    }

    /// <summary>
    /// The displayed power reads the slot the unit's power type names.
    /// </summary>
    /// <remarks>
    /// The fixture creature is unit class 1 — a warrior — so its resource is rage. Its
    /// <see cref="GameUnit.Power"/> must be the rage slot, not the mana one.
    /// </remarks>
    [Fact]
    public void TheDisplayedPower_IsTheSlotThePowerTypeNames()
    {
        Creature creature = CreatureFixture.Build();

        Assert.Equal(GameUnit.PowerRage, creature.PowerType);

        creature.Power = 400;

        Assert.Equal(400u, creature.GetPower(GameUnit.PowerRage));
        Assert.Equal(0u, creature.GetPower(GameUnit.PowerMana));
    }

    /// <summary>A warrior's rage bar has a cap, so the client does not draw 0 / 0.</summary>
    [RequiresClientDataFact]
    public void AClassWhoseResourceIsNotMana_StillGetsACap()
    {
        (Map _, Player warrior, Creature _, MapCombatFixture.Link _) = MapCombatFixture.Engaged();

        Assert.Equal(GameUnit.PowerRage, warrior.PowerType);
        Assert.True(warrior.GetMaxPower(GameUnit.PowerRage) > 0, "the rage bar has no cap");

        // Rage is stored ten times its displayed value, so a full bar is 1000.
        Assert.Equal(1000u, warrior.GetMaxPower(GameUnit.PowerRage));

        // And it starts empty — rage is earned in combat, not handed out.
        Assert.Equal(0u, warrior.GetPower(GameUnit.PowerRage));
    }
}
