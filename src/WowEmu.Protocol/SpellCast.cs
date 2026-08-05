using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>
/// Which optional blocks a cast's target block carries. <c>SpellCastTargetFlags</c>.
/// </summary>
/// <remarks>
/// The mask is read before anything else and decides the entire rest of the block, so a flag the
/// reader does not know about is not a field it can skip — everything after it is lost.
/// </remarks>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "SpellCastTargetFlags is upstream's name; renaming it breaks the trail back to the C++.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "TARGET_FLAG_STRING is upstream's name for the member.")]
public enum SpellCastTargetFlags : uint
{
    None = 0x00000000,

    /// <summary>A packed guid follows: the unit being targeted.</summary>
    Unit = 0x00000002,

    /// <summary>A packed guid follows: an item.</summary>
    Item = 0x00000010,

    /// <summary>A packed guid and three floats follow.</summary>
    SourceLocation = 0x00000020,

    /// <summary>A packed guid and three floats follow.</summary>
    DestLocation = 0x00000040,

    CorpseEnemy = 0x00000200,
    GameObject = 0x00000800,
    TradeItem = 0x00001000,

    /// <summary>A null-terminated string follows.</summary>
    String = 0x00002000,

    CorpseAlly = 0x00008000,
    UnitMinipet = 0x00010000,

    /// <summary>Flags whose presence means a packed guid is in the object slot.</summary>
    /// <remarks>
    /// Five different flags share one guid field. Reading a guid per flag rather than once for the
    /// group consumes four extra guids that are not there.
    /// </remarks>
    ObjectTarget = Unit | GameObject | CorpseEnemy | CorpseAlly | UnitMinipet,

    /// <summary>Flags whose presence means a packed guid is in the item slot.</summary>
    ItemTarget = Item | TradeItem,
}

/// <summary>What a cast is aimed at.</summary>
/// <param name="Mask">Which of the following are present.</param>
/// <param name="ObjectTarget">The unit, gameobject or corpse, when the mask says so.</param>
/// <param name="ItemTarget">The item, when the mask says so.</param>
/// <param name="Source">Where the spell comes from, for spells that care.</param>
/// <param name="Destination">Where it lands, for ground-targeted spells.</param>
/// <param name="StringTarget">A name, for the handful of spells targeted by one.</param>
public readonly record struct SpellCastTargets(
    SpellCastTargetFlags Mask,
    ObjectGuid ObjectTarget = default,
    ObjectGuid ItemTarget = default,
    Position Source = default,
    Position Destination = default,
    string? StringTarget = null)
{
    /// <summary>Whether a unit, gameobject or corpse is named.</summary>
    public bool HasObjectTarget => (Mask & SpellCastTargetFlags.ObjectTarget) != 0;

    /// <summary>Whether a point on the ground is named.</summary>
    public bool HasDestination => (Mask & SpellCastTargetFlags.DestLocation) != 0;

    /// <summary>A cast aimed at nothing in particular — self-cast, or an area centred on the caster.</summary>
    public static SpellCastTargets Self => new(SpellCastTargetFlags.None);
}

/// <summary>Flags on an outgoing cast. <c>SpellCastFlags</c>.</summary>
/// <remarks>Only the ones a plain cast sets; the enum has around twenty-four.</remarks>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "SpellCastFlags is upstream's name; renaming it breaks the trail back to the C++.")]
public enum SpellCastFlags : uint
{
    None = 0x00000000,
    Pending = 0x00000001,
    HasTrajectory = 0x00000002,
    Projectile = 0x00000020,

    /// <summary>Set on every <c>SMSG_SPELL_GO</c>; upstream calls it unknown 9.</summary>
    Unknown9 = 0x00000100,

    /// <summary>A <c>uint32</c> of the caster's remaining power follows.</summary>
    PowerLeftSelf = 0x00000800,

    AdjustMissile = 0x00020000,

    /// <summary>The spell does not trigger the global cooldown.</summary>
    NoGcd = 0x00040000,

    RuneList = 0x00200000,
}

/// <summary>
/// Why a cast was refused. <c>SpellCastResult</c>.
/// </summary>
/// <remarks>
/// Only the results this pipeline can produce. The client turns each into its own error text, so
/// sending a plausible-but-wrong one produces a message that misdirects rather than an obvious bug.
/// </remarks>
public enum SpellCastResult : byte
{
    BadTargets = 12,
    LineOfSight = 47,
    Moving = 51,
    NotKnown = 63,
    NotReady = 67,
    NoPower = 85,
    OutOfRange = 97,
    SpellInProgress = 105,
    TargetsDead = 109,
    TooClose = 128,
    UnitNotInFront = 134,

    /// <summary>
    /// Not a failure and never sent.
    /// </summary>
    /// <remarks>
    /// 255 is upstream's own sentinel, outside the client's range. Writing it to the wire would have
    /// the client look up an error string that does not exist.
    /// </remarks>
    Ok = 255,
}

/// <summary>
/// Reads <c>CMSG_CAST_SPELL</c> and writes the packets a cast produces.
/// </summary>
/// <remarks>
/// Port of <c>SpellCastTargets::Read</c>/<c>Write</c>, <c>Spell::SendSpellStart</c>,
/// <c>Spell::SendSpellGo</c> and <c>Spell::WriteCastResultInfo</c>.
/// <para>
/// <b>Two packets per cast, not one.</b> <c>SMSG_SPELL_START</c> opens the cast bar and
/// <c>SMSG_SPELL_GO</c> closes it and plays the impact. An instant spell sends both, back to back —
/// skipping the start because there is nothing to fill in leaves the client without the animation.
/// </para>
/// </remarks>
public static class SpellCast
{
    /// <summary>
    /// Reads the body of <c>CMSG_CAST_SPELL</c>.
    /// </summary>
    /// <remarks>
    /// The cast count is the client's own handle for this attempt. It has to come back on every
    /// answer — a failure quoting the wrong count leaves the client's button stuck lit.
    /// </remarks>
    public static bool TryRead(
        ref PacketReader reader, out byte castCount, out uint spellId, out SpellCastTargets targets)
    {
        castCount = 0;
        spellId = 0;
        targets = default;

        if (!reader.TryReadUInt8(out castCount)
            || !reader.TryReadUInt32(out spellId)
            || !reader.TryReadUInt8(out byte _))
        {
            return false;
        }

        return TryReadTargets(ref reader, out targets);
    }

    /// <summary>
    /// Reads the target block.
    /// </summary>
    /// <remarks>
    /// Positional and driven entirely by the mask. Note that five separate flags share the single
    /// object-guid field, and two more share the item one — reading a guid per flag consumes bytes
    /// that were never sent.
    /// </remarks>
    public static bool TryReadTargets(ref PacketReader reader, out SpellCastTargets targets)
    {
        targets = default;

        if (!reader.TryReadUInt32(out uint rawMask))
        {
            return false;
        }

        SpellCastTargetFlags mask = (SpellCastTargetFlags)rawMask;

        if (mask == SpellCastTargetFlags.None)
        {
            targets = new SpellCastTargets(mask);
            return true;
        }

        ObjectGuid objectTarget = default;
        ObjectGuid itemTarget = default;
        Position source = default;
        Position destination = default;
        string? stringTarget = null;

        if ((mask & SpellCastTargetFlags.ObjectTarget) != 0 && !reader.TryReadPackedGuid(out objectTarget))
        {
            return false;
        }

        if ((mask & SpellCastTargetFlags.ItemTarget) != 0 && !reader.TryReadPackedGuid(out itemTarget))
        {
            return false;
        }

        if ((mask & SpellCastTargetFlags.SourceLocation) != 0 && !TryReadPoint(ref reader, out source))
        {
            return false;
        }

        if ((mask & SpellCastTargetFlags.DestLocation) != 0 && !TryReadPoint(ref reader, out destination))
        {
            return false;
        }

        if ((mask & SpellCastTargetFlags.String) != 0 && !reader.TryReadCString(out stringTarget))
        {
            return false;
        }

        targets = new SpellCastTargets(mask, objectTarget, itemTarget, source, destination, stringTarget);
        return true;
    }

    /// <summary>Writes the target block back out, in the same order it is read.</summary>
    public static void WriteTargets(PacketWriter writer, in SpellCastTargets targets)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32((uint)targets.Mask);

        if ((targets.Mask & SpellCastTargetFlags.ObjectTarget) != 0)
        {
            writer.WritePackedGuid(targets.ObjectTarget);
        }

        if ((targets.Mask & SpellCastTargetFlags.ItemTarget) != 0)
        {
            writer.WritePackedGuid(targets.ItemTarget);
        }

        if ((targets.Mask & SpellCastTargetFlags.SourceLocation) != 0)
        {
            WritePoint(writer, targets.Source);
        }

        if ((targets.Mask & SpellCastTargetFlags.DestLocation) != 0)
        {
            WritePoint(writer, targets.Destination);
        }

        if ((targets.Mask & SpellCastTargetFlags.String) != 0)
        {
            writer.WriteCString(targets.StringTarget ?? string.Empty);
        }
    }

    /// <summary>
    /// Writes <c>SMSG_SPELL_START</c> — the client opens a cast bar.
    /// </summary>
    /// <remarks>
    /// <b>The caster guid is written twice.</b> The first is whatever is producing the cast — an
    /// item, if one was used — and the second is always the unit. Writing it once produces a packet
    /// the client reads with every field shifted by a guid.
    /// </remarks>
    /// <param name="castTimeMs">Milliseconds the bar should run for. Zero for an instant cast.</param>
    /// <param name="powerLeft">
    /// The caster's remaining power. Only written when <paramref name="flags"/> carries
    /// <see cref="SpellCastFlags.PowerLeftSelf"/>.
    /// </param>
    public static void WriteSpellStart(
        PacketWriter writer,
        ObjectGuid caster,
        byte castCount,
        uint spellId,
        SpellCastFlags flags,
        int castTimeMs,
        in SpellCastTargets targets,
        uint powerLeft = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePackedGuid(caster);
        writer.WritePackedGuid(caster);
        writer.WriteUInt8(castCount);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32((uint)flags);
        writer.WriteUInt32((uint)castTimeMs);

        WriteTargets(writer, targets);

        if ((flags & SpellCastFlags.PowerLeftSelf) != 0)
        {
            writer.WriteUInt32(powerLeft);
        }
    }

    /// <summary>
    /// Writes <c>SMSG_SPELL_GO</c> — the client closes the cast bar and plays the impact.
    /// </summary>
    /// <remarks>
    /// The hit and miss lists are each a <c>uint8</c> count followed by <b>full</b> guids, not
    /// packed ones — the one place in this packet that does not pack. A miss carries a reason byte
    /// after its guid; a hit does not, so the two lists have different strides.
    /// </remarks>
    /// <param name="hits">Everything the spell landed on.</param>
    /// <param name="misses">Everything it did not, each with its reason.</param>
    public static void WriteSpellGo(
        PacketWriter writer,
        ObjectGuid caster,
        byte castCount,
        uint spellId,
        SpellCastFlags flags,
        uint timestampMs,
        IReadOnlyList<ObjectGuid> hits,
        IReadOnlyList<(ObjectGuid Target, byte Reason)> misses,
        in SpellCastTargets targets,
        uint powerLeft = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(misses);

        writer.WritePackedGuid(caster);
        writer.WritePackedGuid(caster);
        writer.WriteUInt8(castCount);
        writer.WriteUInt32(spellId);
        writer.WriteUInt32((uint)flags);
        writer.WriteUInt32(timestampMs);

        // Both counts are a single byte, which caps a spell at 255 targets each way. Upstream stops
        // there deliberately: sending more overflows the count and the client reads the overflow as
        // the start of the next field.
        writer.WriteUInt8((byte)Math.Min(hits.Count, byte.MaxValue));

        foreach (ObjectGuid hit in hits.Take(byte.MaxValue))
        {
            writer.WriteUInt64(hit.Value);
        }

        writer.WriteUInt8((byte)Math.Min(misses.Count, byte.MaxValue));

        foreach ((ObjectGuid target, byte reason) in misses.Take(byte.MaxValue))
        {
            writer.WriteUInt64(target.Value);
            writer.WriteUInt8(reason);
        }

        WriteTargets(writer, targets);

        if ((flags & SpellCastFlags.PowerLeftSelf) != 0)
        {
            writer.WriteUInt32(powerLeft);
        }

        // A ground-targeted cast ends with one more byte. Upstream sends it unconditionally when the
        // destination flag is set and never explains it; omitting it truncates the packet.
        if (targets.HasDestination)
        {
            writer.WriteUInt8(0);
        }
    }

    /// <summary>
    /// Writes <c>SMSG_CAST_FAILED</c> — the client unlights the button and prints an error.
    /// </summary>
    /// <remarks>
    /// Several results carry an extra field the client reads, but none of the ones this pipeline
    /// produces do. Adding one to a result that does not expect it desynchronises the stream.
    /// </remarks>
    public static void WriteCastFailed(
        PacketWriter writer, byte castCount, uint spellId, SpellCastResult result)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt8(castCount);
        writer.WriteUInt32(spellId);
        writer.WriteUInt8((byte)result);
    }

    private static bool TryReadPoint(ref PacketReader reader, out Position position)
    {
        position = default;

        // A packed guid for the transport the point is relative to, then the point. Zero means the
        // world, which is every case until transports exist.
        if (!reader.TryReadPackedGuid(out ObjectGuid _)
            || !reader.TryReadSingle(out float x)
            || !reader.TryReadSingle(out float y)
            || !reader.TryReadSingle(out float z))
        {
            return false;
        }

        position = new Position(x, y, z, 0f);
        return true;
    }

    private static void WritePoint(PacketWriter writer, Position position)
    {
        writer.WritePackedGuid(ObjectGuid.Empty);
        writer.WriteSingle(position.X);
        writer.WriteSingle(position.Y);
        writer.WriteSingle(position.Z);
    }
}
