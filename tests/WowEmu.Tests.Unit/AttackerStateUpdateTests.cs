using WowEmu.Core;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The wire layout of <c>SMSG_ATTACKERSTATEUPDATE</c> and its two companions.
/// </summary>
/// <remarks>
/// Read back field by field rather than compared against a captured blob. The packet's layout is
/// conditional on <c>HitInfo</c>, so what matters is which fields are present and in what order —
/// and a blob comparison says "these bytes differ" where a read-back says "the block trailer is
/// missing".
/// </remarks>
public sealed class AttackerStateUpdateTests
{
    private static readonly ObjectGuid Attacker = ObjectGuid.Create(HighGuid.Player, 42);
    private static readonly ObjectGuid Target = ObjectGuid.Create(HighGuid.Unit, 299, 1234);

    private static byte[] Write(in AttackerState state)
    {
        PacketWriter writer = new();
        AttackerStateUpdate.Write(writer, state);

        return writer.WrittenSpan.ToArray();
    }

    private static AttackerState Swing(
        uint hitInfo = 0, uint damage = 100, uint targetHealth = 1000,
        uint absorbed = 0, uint resisted = 0, uint blocked = 0) =>
        new(hitInfo, Attacker, Target, damage, targetHealth, VictimState: 1, absorbed, resisted, blocked);

    /// <summary>The fixed part of the packet, in order.</summary>
    [Fact]
    public void APlainHit_WritesTheFixedLayout()
    {
        byte[] bytes = Write(Swing(damage: 137));
        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadUInt32(out uint hitInfo));
        Assert.Equal(0u, hitInfo);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid attacker));
        Assert.Equal(Attacker, attacker);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid target));
        Assert.Equal(Target, target);

        Assert.True(reader.TryReadUInt32(out uint fullDamage));
        Assert.Equal(137u, fullDamage);

        Assert.True(reader.TryReadUInt32(out uint overkill));
        Assert.Equal(0u, overkill);

        Assert.True(reader.TryReadUInt8(out byte subDamageCount));
        Assert.Equal(1, subDamageCount);

        Assert.True(reader.TryReadUInt32(out uint schoolMask));
        Assert.Equal(1u, schoolMask);

        // The same figure twice, as a float and then as an integer.
        Assert.True(reader.TryReadSingle(out float asFloat));
        Assert.Equal(137f, asFloat);

        Assert.True(reader.TryReadUInt32(out uint asInteger));
        Assert.Equal(137u, asInteger);

        Assert.True(reader.TryReadUInt8(out byte victimState));
        Assert.Equal(1, victimState);

        // Unknown attacker state, then the melee spell id. Both always zero.
        Assert.True(reader.TryReadUInt32(out uint unknown));
        Assert.Equal(0u, unknown);

        Assert.True(reader.TryReadUInt32(out uint meleeSpellId));
        Assert.Equal(0u, meleeSpellId);

        // Nothing trailing: no flags were set, so no trailers.
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// Overkill is what the hit wasted on an already-dead target, and is never negative.
    /// </summary>
    /// <remarks>
    /// The health passed in is the target's <i>before</i> the hit, because upstream sends this packet
    /// and only then applies the damage. Using post-hit health makes every killing blow report its
    /// whole damage as overkill.
    /// </remarks>
    [Theory]
    [InlineData(50u, 1000u, 0u)]      // nowhere near lethal
    [InlineData(1000u, 1000u, 0u)]    // exactly lethal, nothing wasted
    [InlineData(1500u, 1000u, 500u)]  // 500 wasted
    public void Overkill_IsTheWastedPortionAndNeverNegative(uint damage, uint health, uint expected)
    {
        byte[] bytes = Write(Swing(damage: damage, targetHealth: health));
        PacketReader reader = new(bytes);

        reader.Skip(4);
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        Assert.True(reader.TryReadUInt32(out uint _));

        Assert.True(reader.TryReadUInt32(out uint overkill));
        Assert.Equal(expected, overkill);
    }

    /// <summary>
    /// A blocked swing carries the blocked amount; an unblocked one does not.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is not a parse error. An extra or missing trailer shifts every
    /// byte after it, and since the trailers are at the end the client usually notices several
    /// packets later — which makes it look like whatever came next is broken.
    /// </remarks>
    [Fact]
    public void TheBlockTrailer_FollowsItsFlag()
    {
        byte[] without = Write(Swing(blocked: 30));
        byte[] with = Write(Swing(hitInfo: AttackerStateUpdate.HitInfoBlock, blocked: 30));

        Assert.Equal(without.Length + 4, with.Length);

        // The blocked amount is the last four bytes, and is only there because the flag was set.
        Assert.Equal(30u, BitConverter.ToUInt32(with, with.Length - 4));
    }

    [Theory]
    [InlineData(AttackerStateUpdate.HitInfoFullAbsorb)]
    [InlineData(AttackerStateUpdate.HitInfoPartialAbsorb)]
    public void TheAbsorbTrailer_FollowsEitherAbsorbFlag(uint flag)
    {
        byte[] without = Write(Swing(absorbed: 25));
        byte[] with = Write(Swing(hitInfo: flag, absorbed: 25));

        Assert.Equal(without.Length + 4, with.Length);
    }

    [Theory]
    [InlineData(AttackerStateUpdate.HitInfoFullResist)]
    [InlineData(AttackerStateUpdate.HitInfoPartialResist)]
    public void TheResistTrailer_FollowsEitherResistFlag(uint flag)
    {
        byte[] without = Write(Swing(resisted: 25));
        byte[] with = Write(Swing(hitInfo: flag, resisted: 25));

        Assert.Equal(without.Length + 4, with.Length);
    }

    /// <summary>
    /// The absorb trailer comes before the resist trailer, and both before the victim state.
    /// </summary>
    /// <remarks>
    /// Ordering is the whole content of a conditional layout. Writing resist first would produce a
    /// packet of exactly the right length with two fields swapped.
    /// </remarks>
    [Fact]
    public void TheTrailers_AreInTheOrderTheClientReadsThem()
    {
        const uint HitInfo = AttackerStateUpdate.HitInfoFullAbsorb | AttackerStateUpdate.HitInfoFullResist;

        byte[] bytes = Write(Swing(hitInfo: HitInfo, absorbed: 11, resisted: 22));
        PacketReader reader = new(bytes);

        reader.Skip(4);
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        reader.Skip(4 + 4 + 1 + 4 + 4 + 4);   // damage, overkill, count, school, float, integer

        Assert.True(reader.TryReadUInt32(out uint absorbed));
        Assert.Equal(11u, absorbed);

        Assert.True(reader.TryReadUInt32(out uint resisted));
        Assert.Equal(22u, resisted);

        Assert.True(reader.TryReadUInt8(out byte victimState));
        Assert.Equal(1, victimState);
    }

    [Fact]
    public void TheRageTrailer_FollowsItsFlag()
    {
        byte[] without = Write(Swing());
        byte[] with = Write(Swing(hitInfo: AttackerStateUpdate.HitInfoRageGain));

        Assert.Equal(without.Length + 4, with.Length);
    }

    /// <summary>The block trailer comes before the rage one.</summary>
    [Fact]
    public void BlockPrecedesRage()
    {
        const uint HitInfo = AttackerStateUpdate.HitInfoBlock | AttackerStateUpdate.HitInfoRageGain;

        byte[] bytes = Write(Swing(hitInfo: HitInfo, blocked: 77));

        // Blocked amount, then the rage field — so 77 is the second-to-last word, not the last.
        Assert.Equal(77u, BitConverter.ToUInt32(bytes, bytes.Length - 8));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, bytes.Length - 4));
    }

    // ------------------------------------------------------------------ start and stop

    /// <summary>
    /// <c>SMSG_ATTACKSTART</c> uses full guids where almost everything else packs them.
    /// </summary>
    /// <remarks>
    /// A genuine inconsistency in the protocol rather than a mistake to correct. Packing them makes
    /// the packet shorter than the client expects and the attack animation never starts.
    /// </remarks>
    [Fact]
    public void AttackStart_UsesFullGuids()
    {
        PacketWriter writer = new();
        AttackerStateUpdate.WriteAttackStart(writer, Attacker, Target);

        byte[] bytes = writer.WrittenSpan.ToArray();

        Assert.Equal(16, bytes.Length);
        Assert.Equal(Attacker.Value, BitConverter.ToUInt64(bytes, 0));
        Assert.Equal(Target.Value, BitConverter.ToUInt64(bytes, 8));
    }

    /// <summary><c>SMSG_ATTACKSTOP</c> packs its guids, unlike its counterpart.</summary>
    [Fact]
    public void AttackStop_PacksItsGuids()
    {
        PacketWriter writer = new();
        AttackerStateUpdate.WriteAttackStop(writer, Attacker, Target, victimIsDead: true);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid attacker));
        Assert.Equal(Attacker, attacker);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid target));
        Assert.Equal(Target, target);

        Assert.True(reader.TryReadUInt32(out uint dead));
        Assert.Equal(1u, dead);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// With no victim the packet ends early rather than sending zeros.
    /// </summary>
    /// <remarks>
    /// A shorter packet, not a padded one. Writing a zero guid and a zero flag would be the same
    /// length as a real stop against guid zero, which the client has no way to tell apart.
    /// </remarks>
    [Fact]
    public void AttackStop_WithNoVictim_EndsEarly()
    {
        PacketWriter writer = new();
        AttackerStateUpdate.WriteAttackStop(writer, Attacker, victim: null, victimIsDead: false);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid attacker));
        Assert.Equal(Attacker, attacker);
        Assert.Equal(0, reader.Remaining);
    }
}
