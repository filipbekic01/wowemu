using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>
/// Everything <c>SMSG_ATTACKERSTATEUPDATE</c> carries about one swing.
/// </summary>
/// <remarks>
/// The protocol's own view of a swing, deliberately separate from the game layer's — this assembly
/// names opcodes and knows nothing about attack tables, and the game layer computes outcomes and
/// names no opcodes. The session maps between them.
/// </remarks>
/// <param name="HitInfo">The <c>HitInfo</c> bits. Decides which optional trailers are present.</param>
/// <param name="Attacker">Who swung.</param>
/// <param name="Target">Who was hit.</param>
/// <param name="Damage">What the target loses.</param>
/// <param name="TargetHealth">
/// The target's health <b>before</b> the hit lands. Upstream sends this packet and only then applies
/// the damage, so the health it reads is the pre-hit value — passing the post-hit health instead
/// makes every killing blow report its full damage as overkill.
/// </param>
/// <param name="VictimState">The <c>VictimState</c> value the client draws.</param>
/// <param name="Absorbed">How much an absorb shield ate.</param>
/// <param name="Resisted">How much was resisted.</param>
/// <param name="Blocked">How much a shield blocked. Only written when the block bit is set.</param>
/// <param name="SchoolMask">Which damage school. 1 is physical.</param>
public readonly record struct AttackerState(
    uint HitInfo,
    ObjectGuid Attacker,
    ObjectGuid Target,
    uint Damage,
    uint TargetHealth,
    byte VictimState,
    uint Absorbed = 0,
    uint Resisted = 0,
    uint Blocked = 0,
    uint SchoolMask = 1);

/// <summary>
/// Writes <c>SMSG_ATTACKERSTATEUPDATE</c> — the packet that makes a swing appear in the combat log.
/// </summary>
/// <remarks>
/// Port of <c>Unit::SendAttackStateUpdate</c>.
/// <para>
/// <b>The layout is conditional on <c>HitInfo</c>.</b> The absorb, resist, block and debug trailers
/// are each present only when their bit is set, and the client reads them positionally. A trailer
/// written without its bit — or a bit set without its trailer — does not produce a parse error; it
/// silently shifts everything after it, and the symptom is a client that disconnects or draws
/// nonsense some swings later. That is why the writer takes the flags rather than inferring them.
/// </para>
/// </remarks>
public static class AttackerStateUpdate
{
    /// <summary>An absorb happened, so the absorb trailer is present.</summary>
    public const uint HitInfoFullAbsorb = 0x00000020;

    /// <inheritdoc cref="HitInfoFullAbsorb"/>
    public const uint HitInfoPartialAbsorb = 0x00000040;

    /// <summary>A resist happened, so the resist trailer is present.</summary>
    public const uint HitInfoFullResist = 0x00000080;

    /// <inheritdoc cref="HitInfoFullResist"/>
    public const uint HitInfoPartialResist = 0x00000100;

    /// <summary>The swing was blocked, so the blocked-amount trailer is present.</summary>
    public const uint HitInfoBlock = 0x00002000;

    /// <summary>The attacker gained rage, so a rage trailer is present.</summary>
    public const uint HitInfoRageGain = 0x00800000;

    /// <summary>
    /// The debug trailer — twelve mostly-float fields no retail server is known to send.
    /// </summary>
    /// <remarks>Never set here. Named so the conditional layout is complete rather than partly implicit.</remarks>
    public const uint HitInfoDebug = 0x00000001;

    /// <summary>Writes the packet body.</summary>
    public static void Write(PacketWriter writer, in AttackerState state)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(state.HitInfo);
        writer.WritePackedGuid(state.Attacker);
        writer.WritePackedGuid(state.Target);

        writer.WriteUInt32(state.Damage);

        // Overkill: how much of the hit was wasted on an already-dead target. The client shows it in
        // the killing blow. Never negative — a non-lethal hit overkills for nothing, not a negative
        // amount, and the subtraction is done in a signed type precisely so it can be clamped.
        long overkill = (long)state.Damage - state.TargetHealth;
        writer.WriteUInt32(overkill < 0 ? 0 : (uint)overkill);

        // One damage school. A player's weapon can carry two, but that needs items.
        writer.WriteUInt8(1);
        writer.WriteUInt32(state.SchoolMask);

        // The same number twice, once as a float and once as an integer. Not a redundancy to clean
        // up: the client reads both, and the float is what the floating combat text animates.
        writer.WriteSingle(state.Damage);
        writer.WriteUInt32(state.Damage);

        if ((state.HitInfo & (HitInfoFullAbsorb | HitInfoPartialAbsorb)) != 0)
        {
            writer.WriteUInt32(state.Absorbed);
        }

        if ((state.HitInfo & (HitInfoFullResist | HitInfoPartialResist)) != 0)
        {
            writer.WriteUInt32(state.Resisted);
        }

        writer.WriteUInt8(state.VictimState);

        // Two fields upstream sends as zero on every path: an unknown attacker state, and the id of
        // the spell that caused the swing (for abilities like Heroic Strike that ride an auto-attack).
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);

        if ((state.HitInfo & HitInfoBlock) != 0)
        {
            writer.WriteUInt32(state.Blocked);
        }

        if ((state.HitInfo & HitInfoRageGain) != 0)
        {
            writer.WriteUInt32(0);
        }
    }

    /// <summary>
    /// Writes <c>SMSG_ATTACKSTART</c> — the client starts playing the attack animation.
    /// </summary>
    /// <remarks>
    /// <b>Full guids, not packed.</b> The one packet in this pair that does not pack them, and the
    /// asymmetry is upstream's rather than an oversight — packing these makes the client read the
    /// victim from the wrong offset and stop animating.
    /// </remarks>
    public static void WriteAttackStart(PacketWriter writer, ObjectGuid attacker, ObjectGuid victim)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(attacker.Value);
        writer.WriteUInt64(victim.Value);
    }

    /// <summary>
    /// Writes <c>SMSG_ATTACKSTOP</c> — the client stops the attack animation.
    /// </summary>
    /// <remarks>
    /// Packed guids here, unlike <see cref="WriteAttackStart"/>. The victim and the dead flag are
    /// omitted entirely when there is no victim — a shorter packet, not a zeroed one.
    /// </remarks>
    /// <param name="victimIsDead">Whether the attack stopped because the victim died.</param>
    public static void WriteAttackStop(
        PacketWriter writer, ObjectGuid attacker, ObjectGuid? victim, bool victimIsDead)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePackedGuid(attacker);

        if (victim is { } target)
        {
            writer.WritePackedGuid(target);
            writer.WriteUInt32(victimIsDead ? 1u : 0u);
        }
    }
}
