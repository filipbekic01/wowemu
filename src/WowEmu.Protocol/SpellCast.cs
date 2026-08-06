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

/// <summary>One spell's damage against one target, for the combat log.</summary>
/// <param name="Target">Who was hit.</param>
/// <param name="Attacker">Who cast it.</param>
/// <param name="SpellId">What was cast.</param>
/// <param name="Damage">Health lost, after mitigation.</param>
/// <param name="TargetHealth">
/// The target's health <b>before</b> the hit, which is what overkill is measured against.
/// </param>
/// <param name="SchoolMask">Which school, so the client colours the number.</param>
/// <param name="Absorbed">How much a shield ate.</param>
/// <param name="Resisted">How much resistance turned aside.</param>
/// <param name="Blocked">How much was blocked.</param>
/// <param name="IsPhysical">
/// Changes which combat-log sentence the client prints — "hit for" against "suffers damage from" —
/// rather than changing any number.
/// </param>
public readonly record struct SpellDamageLog(
    ObjectGuid Target,
    ObjectGuid Attacker,
    uint SpellId,
    uint Damage,
    uint TargetHealth,
    uint SchoolMask,
    uint Absorbed = 0,
    uint Resisted = 0,
    uint Blocked = 0,
    bool IsPhysical = false);

/// <summary>
/// Writes <c>SMSG_SPELLNONMELEEDAMAGELOG</c> — a spell's damage in the combat log.
/// </summary>
/// <remarks>
/// Port of <c>Unit::SendSpellNonMeleeDamageLog</c>.
/// <para>
/// <b>The target comes first, then the attacker</b> — the opposite order to
/// <c>SMSG_ATTACKERSTATEUPDATE</c>, which leads with the attacker. Two packets describing the same
/// kind of event with the operands reversed, and nothing in either says so.
/// </para>
/// </remarks>
public static class SpellDamageLogPacket
{
    /// <summary>Writes the packet body.</summary>
    public static void Write(PacketWriter writer, in SpellDamageLog log)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePackedGuid(log.Target);
        writer.WritePackedGuid(log.Attacker);
        writer.WriteUInt32(log.SpellId);

        writer.WriteUInt32(log.Damage);

        // Never negative: a non-lethal hit overkills for nothing. Computed signed so it can be
        // clamped rather than wrapping to four billion.
        long overkill = (long)log.Damage - log.TargetHealth;
        writer.WriteUInt32(overkill > 0 ? (uint)overkill : 0);

        // A single byte, so only the low eight school bits survive. That is every real school —
        // there are seven — but it means the field is not interchangeable with the uint32 school
        // masks used elsewhere.
        writer.WriteUInt8((byte)log.SchoolMask);

        writer.WriteUInt32(log.Absorbed);
        writer.WriteUInt32(log.Resisted);
        writer.WriteUInt8(log.IsPhysical ? (byte)1 : (byte)0);

        // Unused, and sent as zero by upstream on every path.
        writer.WriteUInt8(0);

        writer.WriteUInt32(log.Blocked);

        // The hit-info word is written twice, then a byte of debug flags. Upstream sends all three
        // unconditionally; the debug byte gates optional float blocks that no retail server sends,
        // so a zero here is what keeps the packet ending where the client expects.
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt8(0);
    }
}

/// <summary>
/// Writes <c>SMSG_LOG_XPGAIN</c> and <c>SMSG_LEVELUP_INFO</c>.
/// </summary>
/// <remarks>
/// Port of <c>Player::SendLogXPGain</c> and <c>WorldPackets::Misc::LevelUpInfo::Write</c>.
/// </remarks>
public static class ExperiencePackets
{
    /// <summary>How many power deltas a level-up carries. One per power type.</summary>
    public const int PowerDeltaCount = 6;

    /// <summary>How many stat deltas a level-up carries — strength through spirit.</summary>
    public const int StatDeltaCount = 5;

    /// <summary>
    /// Writes <c>SMSG_LOG_XPGAIN</c>.
    /// </summary>
    /// <remarks>
    /// <b>The layout depends on whether there was a victim.</b> A kill carries two extra fields —
    /// the unbonused amount and the group rate — that a quest reward does not, and the type byte is
    /// what tells the client which shape it is reading. Sending the kill shape with an empty guid
    /// makes the client read the trailing byte from the wrong place.
    /// </remarks>
    /// <param name="victim">Who died, or empty for experience from something other than a kill.</param>
    /// <param name="amount">The experience gained, excluding any rested bonus.</param>
    /// <param name="bonus">Rested bonus on top.</param>
    public static void WriteLogXpGain(
        PacketWriter writer, ObjectGuid victim, uint amount, uint bonus = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // A full guid, not a packed one.
        writer.WriteUInt64(victim.Value);

        writer.WriteUInt32(amount + bonus);

        // 0 is a kill, 1 is anything else. The empty guid and this byte have to agree.
        bool fromKill = !victim.IsEmpty;
        writer.WriteUInt8(fromKill ? (byte)0 : (byte)1);

        if (fromKill)
        {
            writer.WriteUInt32(amount);

            // The group rate. 1 means no group bonus; upstream sends 1 unconditionally with a
            // comment saying it cannot work out how to compute the real one.
            writer.WriteSingle(1f);
        }

        // Whether the amount includes a recruit-a-friend bonus.
        writer.WriteUInt8(0);
    }

    /// <summary>
    /// Writes <c>SMSG_LEVELUP_INFO</c> — the numbers the client animates on the level-up banner.
    /// </summary>
    /// <remarks>
    /// Every field is a <b>delta</b>, not a new total. Sending totals produces a banner claiming the
    /// character gained its entire health pool, and the fields are unsigned on the wire so a
    /// decrease wraps rather than showing negative.
    /// </remarks>
    /// <param name="powerDeltas">One per power type; only mana is ever non-zero.</param>
    /// <param name="statDeltas">Strength, agility, stamina, intellect, spirit.</param>
    public static void WriteLevelUp(
        PacketWriter writer,
        uint newLevel,
        int healthDelta,
        ReadOnlySpan<int> powerDeltas,
        ReadOnlySpan<int> statDeltas)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(newLevel);
        writer.WriteUInt32((uint)healthDelta);

        // Fixed counts, so a short span is padded rather than truncating the packet.
        for (int i = 0; i < PowerDeltaCount; i++)
        {
            writer.WriteUInt32(i < powerDeltas.Length ? (uint)powerDeltas[i] : 0);
        }

        for (int i = 0; i < StatDeltaCount; i++)
        {
            writer.WriteUInt32(i < statDeltas.Length ? (uint)statDeltas[i] : 0);
        }
    }
}

/// <summary>One aura as the client is told about it.</summary>
/// <param name="Slot">Which of the target's aura slots. The client keys everything off this.</param>
/// <param name="SpellId">Zero means "the slot is now empty" and nothing else follows.</param>
/// <param name="Flags">Decides which of the optional trailers are present.</param>
/// <param name="CasterLevel">Shown on the tooltip.</param>
/// <param name="StackAmount">Never zero — the client draws a zero stack as no aura at all.</param>
/// <param name="Caster">Written only when the target did <b>not</b> cast it on itself.</param>
/// <param name="MaxDurationMs">Written only with the duration flag.</param>
/// <param name="RemainingMs">As above.</param>
public readonly record struct AuraSlotUpdate(
    byte Slot,
    uint SpellId,
    byte Flags,
    byte CasterLevel,
    byte StackAmount,
    ObjectGuid Caster = default,
    int MaxDurationMs = 0,
    int RemainingMs = 0);

/// <summary>
/// Writes <c>SMSG_INITIAL_SPELLS</c> — the whole spellbook, in one packet at login.
/// </summary>
/// <remarks>
/// Port of <c>Player::SendInitialSpells</c>. The client builds its spellbook and action bars from
/// this and nothing else: without it a character knows nothing it can cast, whatever the server
/// thinks.
/// </remarks>
public static class InitialSpells
{
    public static void Write(PacketWriter writer, IReadOnlyCollection<uint> spells)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(spells);

        // A leading zero byte whose meaning nobody has established. The client reads it.
        writer.WriteUInt8(0);

        // Sixteen bits, not thirty-two. A spellbook of more than 65,535 is not a thing, but writing
        // a word here shifts every spell that follows.
        writer.WriteUInt16((ushort)spells.Count);

        foreach (uint spellId in spells)
        {
            writer.WriteUInt32(spellId);

            // Upstream's comment says "it's not slot id" and writes zero. Reproduced rather than
            // guessed at.
            writer.WriteUInt16(0);
        }

        // Spell cooldowns. None survive a logout here, so the count is always zero — but the count
        // itself is not optional, and leaving it off truncates the packet.
        writer.WriteUInt16(0);
    }

    /// <summary>Writes <c>SMSG_LEARNED_SPELL</c> — one spell, learned just now.</summary>
    public static void WriteLearned(PacketWriter writer, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(spellId);
        writer.WriteUInt16(0);
    }
}

/// <summary>
/// Writes <c>SMSG_ACTION_BUTTONS</c>.
/// </summary>
/// <remarks>
/// Port of <c>Player::SendActionButtons</c>. <b>All 144 buttons are written, empty ones included</b>
/// — there is no count and no index, so the client reads them positionally and a short packet
/// leaves the tail of the bars filled with whatever came next.
/// </remarks>
public static class ActionButtons
{
    /// <summary>How many buttons the client has. <c>MAX_ACTION_BUTTONS</c>.</summary>
    public const int MaxButtons = 144;

    /// <summary>
    /// The state byte. <c>1</c> is what upstream sends with real data.
    /// </summary>
    /// <remarks>
    /// Zero is the documented "initial" value and upstream notes it "had some difficulties", so it
    /// sends 1 in both cases. Two clears the bars and is followed by no data at all.
    /// </remarks>
    public const byte StateInitial = 1;

    /// <inheritdoc cref="StateInitial"/>
    public const byte StateClear = 2;

    public static void Write(PacketWriter writer, IReadOnlyDictionary<byte, uint> buttons, byte state = StateInitial)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(buttons);

        writer.WriteUInt8(state);

        if (state == StateClear)
        {
            return;
        }

        for (int button = 0; button < MaxButtons; button++)
        {
            writer.WriteUInt32(buttons.GetValueOrDefault((byte)button));
        }
    }
}

/// <summary>One periodic aura tick, held until the session's next flush.</summary>
/// <param name="Overflow">Overkill for a damage tick, overhealing for a heal.</param>
public readonly record struct PeriodicAuraLog(
    ObjectGuid Target,
    ObjectGuid Caster,
    uint SpellId,
    uint AuraType,
    uint Amount,
    uint Overflow,
    uint SchoolMask);

/// <summary>
/// Writes <c>SMSG_AURA_UPDATE</c> and <c>SMSG_PERIODICAURALOG</c>.
/// </summary>
/// <remarks>
/// Port of <c>AuraApplication::BuildUpdatePacket</c> and <c>Unit::SendPeriodicAuraLog</c>.
/// <para>
/// <b>3.3.5a has no aura update fields.</b> Earlier clients carried auras in the unit's own field
/// block; this one learns about them through these packets alone, which is why a slot number is the
/// only handle either side has.
/// </para>
/// </remarks>
public static class AuraUpdate
{
    /// <summary>The caster cast it on itself, so no caster guid is written.</summary>
    public const byte FlagCaster = 0x08;

    /// <summary>Two duration words follow.</summary>
    public const byte FlagDuration = 0x20;

    /// <summary>
    /// Writes an aura landing or changing.
    /// </summary>
    /// <remarks>
    /// Which trailers appear is decided entirely by <paramref name="update"/>'s flags, so they have
    /// to describe what is actually written — a duration flag with no duration behind it leaves the
    /// client reading the next packet's bytes as a timer.
    /// </remarks>
    public static void WriteApplied(PacketWriter writer, ObjectGuid target, in AuraSlotUpdate update)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePackedGuid(target);
        writer.WriteUInt8(update.Slot);
        writer.WriteUInt32(update.SpellId);
        writer.WriteUInt8(update.Flags);
        writer.WriteUInt8(update.CasterLevel);

        // Never zero: the client treats a zero stack as an aura that is not there and draws nothing.
        writer.WriteUInt8(Math.Max(update.StackAmount, (byte)1));

        if ((update.Flags & FlagCaster) == 0)
        {
            writer.WritePackedGuid(update.Caster);
        }

        if ((update.Flags & FlagDuration) != 0)
        {
            writer.WriteUInt32((uint)Math.Max(update.MaxDurationMs, 0));
            writer.WriteUInt32((uint)Math.Max(update.RemainingMs, 0));
        }
    }

    /// <summary>
    /// Writes an aura going away.
    /// </summary>
    /// <remarks>
    /// A spell id of zero <i>is</i> the removal, and the packet ends there. Sending the full body
    /// with a zero id instead leaves four bytes the client reads as the start of something else.
    /// </remarks>
    public static void WriteRemoved(PacketWriter writer, ObjectGuid target, byte slot)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePackedGuid(target);
        writer.WriteUInt8(slot);
        writer.WriteUInt32(0);
    }

    /// <summary>
    /// Writes <c>SMSG_PERIODICAURALOG</c> — one tick in the combat log.
    /// </summary>
    /// <remarks>
    /// The payload after the aura type differs per type, which is why this takes the type rather
    /// than inferring it: a heal writes three words and a damage tick writes five plus a byte.
    /// </remarks>
    /// <param name="auraType">The <c>AuraType</c>, which selects the trailer's shape.</param>
    public static void WritePeriodicLog(
        PacketWriter writer,
        ObjectGuid target,
        ObjectGuid caster,
        uint spellId,
        uint auraType,
        uint amount,
        uint overflow,
        uint schoolMask)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePackedGuid(target);
        writer.WritePackedGuid(caster);
        writer.WriteUInt32(spellId);

        // A count, always one here. Upstream can batch several effects of the same aura into one
        // packet; nothing produces more than one at a time yet.
        writer.WriteUInt32(1);
        writer.WriteUInt32(auraType);

        const uint PeriodicDamage = 3;
        const uint PeriodicHeal = 8;

        switch (auraType)
        {
            case PeriodicDamage:
                writer.WriteUInt32(amount);
                writer.WriteUInt32(overflow);     // overkill
                writer.WriteUInt32(schoolMask);
                writer.WriteUInt32(0);            // absorbed
                writer.WriteUInt32(0);            // resisted
                writer.WriteUInt8(0);             // critical, new in 3.1.2
                break;

            case PeriodicHeal:
                writer.WriteUInt32(amount);
                writer.WriteUInt32(overflow);     // overheal
                writer.WriteUInt32(0);            // absorbed
                writer.WriteUInt8(0);             // critical
                break;

            default:
                // Anything else takes the power-drain shape, which is a single amount. Writing
                // nothing would truncate the packet mid-record.
                writer.WriteUInt32(amount);
                break;
        }
    }
}
