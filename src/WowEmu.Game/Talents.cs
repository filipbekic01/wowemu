using WowEmu.Data.Client;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>Why a talent could not be learned.</summary>
public enum TalentResult
{
    Ok,

    /// <summary>No such talent, or no such rank of it.</summary>
    Unknown,

    /// <summary>The talent belongs to another class's tree.</summary>
    WrongClass,

    /// <summary>Not enough unspent points.</summary>
    NotEnoughPoints,

    /// <summary>Not enough points spent in this tree to reach the talent's row.</summary>
    RowLocked,

    /// <summary>The talent it depends on has not been taken far enough.</summary>
    MissingPrerequisite,

    /// <summary>This rank, or a higher one, is already known.</summary>
    AlreadyKnown,
}

/// <summary>
/// A character's talents, across both specs.
/// </summary>
/// <remarks>
/// Port of <c>Player::LearnTalent</c>, <c>Player::resetTalents</c> and
/// <c>Player::CalculateTalentsPoints</c>.
/// <para>
/// <b>Talent ids and spell ids are different namespaces.</b> The client asks for a talent id and a
/// rank; the spellbook holds the rank's spell. Every operation here translates between the two, and
/// treating either as the other silently learns some unrelated spell.
/// </para>
/// </remarks>
public sealed class PlayerTalents(Player owner)
{
    /// <summary>How many specs a character can hold. <c>MAX_TALENT_SPECS</c>.</summary>
    public const int MaxSpecs = 2;

    /// <summary>The level at which the first talent point is granted.</summary>
    public const byte FirstTalentLevel = 10;

    /// <summary>Which talent ranks each spec holds, by talent id.</summary>
    private readonly Dictionary<uint, byte>[] _bySpec =
        [.. Enumerable.Range(0, MaxSpecs).Select(_ => new Dictionary<uint, byte>())];

    /// <summary>Which spec is being played. Zero or one.</summary>
    public byte ActiveSpec { get; private set; }

    /// <summary>
    /// How many specs this character has bought. One until they pay for dual spec.
    /// </summary>
    /// <remarks>
    /// Sent to the client as the tab count. Reporting two before the character has paid draws a
    /// second, empty tab they can switch to and lose their talents in.
    /// </remarks>
    public byte SpecCount { get; set; } = 1;

    /// <summary>
    /// How many points this character has not spent.
    /// </summary>
    /// <remarks>
    /// An update field rather than a plain number: the client draws the "you have N points" text
    /// from it, and a server-side counter alone leaves the pane insisting there are none.
    /// </remarks>
    public uint FreePoints
    {
        get => owner.Fields.GetUInt32(UpdateFields.PLAYER_CHARACTER_POINTS1);
        set => owner.Fields.SetUInt32(UpdateFields.PLAYER_CHARACTER_POINTS1, value);
    }

    /// <summary>Every talent in a spec, as talent id to rank index (0-based).</summary>
    public IReadOnlyDictionary<uint, byte> InSpec(int spec) =>
        spec >= 0 && spec < MaxSpecs ? _bySpec[spec] : _bySpec[0];

    /// <summary>Every talent in the spec being played.</summary>
    public IReadOnlyDictionary<uint, byte> Active => _bySpec[ActiveSpec];

    /// <summary>
    /// How many talent points a character of this level should have in total.
    /// </summary>
    /// <remarks>
    /// <b>Level minus nine, and nothing below level 10.</b> A naive <c>level - 9</c> goes negative
    /// for a level-1 character and, unsigned, hands out four billion points.
    /// <para>
    /// Death knights are the exception upstream: they get <c>level - 55</c> while in Ebon Hold plus
    /// their quest-granted points, capped at the ordinary figure. That is a starting-zone
    /// restriction rather than a class rule — outside Ebon Hold they use the ordinary count.
    /// </remarks>
    public static uint PointsForLevel(byte level) =>
        level < FirstTalentLevel ? 0u : (uint)(level - (FirstTalentLevel - 1));

    /// <summary>
    /// How many points are spent in one tree, in the spec being played.
    /// </summary>
    /// <remarks>
    /// <b>Rank index plus one</b>, because a talent at rank 0 is one point spent, not zero. Summing
    /// the raw ranks undercounts every tree by the number of talents in it, which quietly unlocks
    /// deeper rows later than it should.
    /// </remarks>
    public uint SpentIn(uint tabId, DbcStore<TalentEntry> talents)
    {
        ArgumentNullException.ThrowIfNull(talents);

        uint spent = 0;

        foreach ((uint talentId, byte rank) in _bySpec[ActiveSpec])
        {
            if (talents.TryGet(talentId, out TalentEntry? entry)
                && entry is not null
                && entry.TabId == tabId)
            {
                spent += rank + 1u;
            }
        }

        return spent;
    }

    /// <summary>How many points are spent across every tree, in the spec being played.</summary>
    public uint TotalSpent()
    {
        uint spent = 0;

        foreach (byte rank in _bySpec[ActiveSpec].Values)
        {
            spent += rank + 1u;
        }

        return spent;
    }

    /// <summary>
    /// Learns a rank of a talent.
    /// </summary>
    /// <param name="rank">Zero-based, as the client sends it.</param>
    /// <returns>The spells to add to the character, or an empty list on refusal.</returns>
    /// <remarks>
    /// <b>Learning rank 3 from nothing costs three points, not one.</b> The client sends the rank
    /// it wants rather than one increment, so the cost is the difference from what is already
    /// known — charging one point per click lets a player fill a tree for a fraction of its cost.
    /// </remarks>
    public TalentResult Learn(
        uint talentId,
        byte rank,
        DbcStore<TalentEntry> talents,
        DbcStore<TalentTabEntry> tabs,
        out uint spellId)
    {
        ArgumentNullException.ThrowIfNull(talents);
        ArgumentNullException.ThrowIfNull(tabs);

        spellId = 0;

        if (rank >= TalentEntry.MaxRank
            || !talents.TryGet(talentId, out TalentEntry? talent)
            || talent is null)
        {
            return TalentResult.Unknown;
        }

        if (!tabs.TryGet(talent.TabId, out TalentTabEntry? tab) || tab is null)
        {
            return TalentResult.Unknown;
        }

        // The client greys out other classes' trees, so nothing legitimate ever asks — which is
        // exactly why it is checked. A modified client asks for whatever it likes.
        if ((ClassMaskOf(owner.Class) & tab.ClassMask) == 0)
        {
            return TalentResult.WrongClass;
        }

        uint spell = talent.SpellFor(rank);

        if (spell == 0)
        {
            return TalentResult.Unknown;
        }

        Dictionary<uint, byte> current = _bySpec[ActiveSpec];

        // Ranks are stored 0-based, so "known" is a lookup rather than a comparison against zero.
        int knownRank = current.TryGetValue(talentId, out byte held) ? held + 1 : 0;

        if (knownRank >= rank + 1)
        {
            return TalentResult.AlreadyKnown;
        }

        uint cost = (uint)(rank + 1 - knownRank);

        if (FreePoints < cost)
        {
            return TalentResult.NotEnoughPoints;
        }

        if (!SatisfiesPrerequisite(talent, talents))
        {
            return TalentResult.MissingPrerequisite;
        }

        // A row is worth five points regardless of how many talents sit on it, so row 3 wants
        // fifteen spent in this tree — not fifteen spent anywhere, and not three talents taken.
        if (talent.Row > 0 && SpentIn(talent.TabId, talents) < talent.Row * TalentEntry.PointsPerRow)
        {
            return TalentResult.RowLocked;
        }

        current[talentId] = rank;
        FreePoints -= cost;
        spellId = spell;

        return TalentResult.Ok;
    }

    /// <summary>
    /// Whether the talent this one depends on has been taken far enough.
    /// </summary>
    /// <remarks>
    /// <b>Any rank at or above the required one satisfies it</b>, so the check walks upwards from
    /// <c>DependsOnRank</c> rather than comparing equality. A talent needing rank 2 of its parent
    /// is satisfied by rank 3, and an equality test refuses it.
    /// </remarks>
    private bool SatisfiesPrerequisite(TalentEntry talent, DbcStore<TalentEntry> talents)
    {
        if (talent.DependsOnTalent == 0)
        {
            return true;
        }

        if (!talents.TryGet(talent.DependsOnTalent, out TalentEntry? parent) || parent is null)
        {
            // The parent is missing from the file. Upstream treats that as satisfied rather than
            // as a refusal, so a data gap does not make a talent permanently unreachable.
            return true;
        }

        return _bySpec[ActiveSpec].TryGetValue(parent.Id, out byte parentRank)
            && parentRank >= talent.DependsOnRank;
    }

    /// <summary>
    /// Forgets every talent in the spec being played.
    /// </summary>
    /// <returns>The spells to take away.</returns>
    /// <remarks>
    /// <b>Only the active spec.</b> Resetting both would wipe the spec the player is not looking
    /// at, which they have no way to notice until they switch to it.
    /// </remarks>
    public IReadOnlyList<uint> Reset(DbcStore<TalentEntry> talents)
    {
        ArgumentNullException.ThrowIfNull(talents);

        List<uint> removed = [];

        foreach ((uint talentId, byte rank) in _bySpec[ActiveSpec])
        {
            if (!talents.TryGet(talentId, out TalentEntry? talent) || talent is null)
            {
                continue;
            }

            // Every rank up to the one held, not just the held one — each rank is its own spell and
            // leaving the lower ones behind keeps a reset character's abilities working.
            for (int i = 0; i <= rank; i++)
            {
                if (talent.SpellFor(i) is var spell and not 0)
                {
                    removed.Add(spell);
                }
            }
        }

        _bySpec[ActiveSpec].Clear();
        FreePoints = PointsForLevel(owner.Level);

        return removed;
    }

    /// <summary>
    /// Switches to the other spec.
    /// </summary>
    /// <returns>The spells to take away, or null when the switch was refused.</returns>
    /// <remarks>
    /// <b>The old spec's talent spells have to come off.</b> They are not in the new spec's build,
    /// and leaving them makes dual spec a way to have both — every talent of both specs, active at
    /// once, for the price of one respec.
    /// </remarks>
    public IReadOnlyList<uint>? Activate(byte spec, DbcStore<TalentEntry> talents)
    {
        ArgumentNullException.ThrowIfNull(talents);

        if (spec >= SpecCount || spec >= MaxSpecs || spec == ActiveSpec)
        {
            return null;
        }

        List<uint> removed = [];

        foreach ((uint talentId, byte rank) in _bySpec[ActiveSpec])
        {
            if (!talents.TryGet(talentId, out TalentEntry? talent) || talent is null)
            {
                continue;
            }

            for (int i = 0; i <= rank; i++)
            {
                if (talent.SpellFor(i) is var spell and not 0)
                {
                    removed.Add(spell);
                }
            }
        }

        ActiveSpec = spec;
        FreePoints = PointsForLevel(owner.Level) - TotalSpent();

        return removed;
    }

    /// <summary>
    /// The spells the spec being played grants, for putting back after a switch.
    /// </summary>
    public IReadOnlyList<uint> ActiveSpells(DbcStore<TalentEntry> talents)
    {
        ArgumentNullException.ThrowIfNull(talents);

        List<uint> spells = [];

        foreach ((uint talentId, byte rank) in _bySpec[ActiveSpec])
        {
            if (talents.TryGet(talentId, out TalentEntry? talent) && talent is not null)
            {
                for (int i = 0; i <= rank; i++)
                {
                    if (talent.SpellFor(i) is var spell and not 0)
                    {
                        spells.Add(spell);
                    }
                }
            }
        }

        return spells;
    }

    /// <summary>Puts a saved talent back, without re-running any of the rules.</summary>
    public void Restore(int spec, uint talentId, byte rank)
    {
        if (spec < 0 || spec >= MaxSpecs)
        {
            return;
        }

        _bySpec[spec][talentId] = rank;
    }

    /// <summary>Sets the active spec at load, without touching the point count.</summary>
    public void RestoreActiveSpec(byte spec)
    {
        if (spec < MaxSpecs)
        {
            ActiveSpec = spec;
        }
    }

    /// <summary>
    /// Recomputes the unspent point count for the character's level.
    /// </summary>
    /// <remarks>
    /// Called on level-up and after loading. <b>Derived, not incremented</b> — a counter bumped on
    /// each level drifts the moment anything else touches it, and the drift is invisible until a
    /// player finds themselves a point short.
    /// </remarks>
    public void Recalculate() => FreePoints = PointsForLevel(owner.Level) - TotalSpent();

    /// <summary>The class mask for a class id, as <c>TalentTab.ClassMask</c> holds it.</summary>
    public static uint ClassMaskOf(byte classId) => classId == 0 ? 0u : 1u << (classId - 1);
}

/// <summary>
/// What a talent reset costs.
/// </summary>
/// <remarks>
/// Port of <c>Player::resetTalentsCost</c>. Escalating, and it decays — the cost is part of the
/// character rather than a fixed price.
/// </remarks>
public static class TalentResetCost
{
    /// <summary>One gold, in copper.</summary>
    public const uint Gold = 10000;

    /// <summary>The ceiling. Fifty gold.</summary>
    public const uint Maximum = 50 * Gold;

    /// <summary>The floor once the ladder has been climbed. Ten gold.</summary>
    public const uint Floor = 10 * Gold;

    /// <summary>A month, in seconds — the interval the cost decays over.</summary>
    public const long MonthSeconds = 30L * 24 * 3600;

    /// <summary>
    /// What the next reset costs, given what the last one cost and when it was.
    /// </summary>
    /// <remarks>
    /// <b>The first three steps are 1, 5 and 10 gold, then 5-gold increments to a cap of 50.</b>
    /// <para>
    /// Past 10 gold it <i>decays</i>: five gold per whole month since the last reset, to a floor of
    /// ten. A character who has not respecced in a year pays ten gold again. Reading the ladder as
    /// monotonic makes respeccing permanently expensive after a few uses, which is the opposite of
    /// what the game does.
    /// </para>
    /// </remarks>
    public static uint Next(uint lastCost, long lastResetTime, long now)
    {
        if (lastCost < Gold)
        {
            return Gold;
        }

        if (lastCost < 5 * Gold)
        {
            return 5 * Gold;
        }

        if (lastCost < Floor)
        {
            return Floor;
        }

        long months = (now - lastResetTime) / MonthSeconds;

        if (months > 0)
        {
            long reduced = lastCost - (5L * Gold * months);

            return reduced < Floor ? Floor : (uint)reduced;
        }

        return Math.Min(lastCost + (5 * Gold), Maximum);
    }
}

/// <summary>
/// A character's glyphs.
/// </summary>
/// <remarks>
/// Port of <c>Player::InitGlyphsForLevel</c> and the glyph half of <c>Player::ApplyGlyph</c>. Six
/// sockets, per spec, unlocked by level.
/// </remarks>
public sealed class PlayerGlyphs(Player owner)
{
    /// <summary>How many glyph sockets there are. <c>MAX_GLYPH_SLOT_INDEX</c>.</summary>
    public const int SlotCount = 6;

    /// <summary>Which glyph is in each socket, per spec.</summary>
    private readonly uint[][] _bySpec =
        [.. Enumerable.Range(0, PlayerTalents.MaxSpecs).Select(_ => new uint[SlotCount])];

    /// <summary>Every glyph in a spec, by socket.</summary>
    public IReadOnlyList<uint> InSpec(int spec) =>
        spec >= 0 && spec < PlayerTalents.MaxSpecs ? _bySpec[spec] : _bySpec[0];

    /// <summary>
    /// Which sockets are unlocked, as the mask the client reads.
    /// </summary>
    /// <remarks>
    /// <b>The bits are not in level order.</b> Level 30 unlocks bit 0x08 and level 50 unlocks
    /// 0x04 — the pane's layout and the unlock order genuinely disagree, and assigning them in
    /// ascending order gives players the wrong sockets at the wrong levels.
    /// </remarks>
    public static uint EnabledMaskFor(byte level)
    {
        uint mask = 0;

        if (level >= 15)
        {
            mask |= 0x01 | 0x02;
        }

        if (level >= 30)
        {
            mask |= 0x08;
        }

        if (level >= 50)
        {
            mask |= 0x04;
        }

        if (level >= 70)
        {
            mask |= 0x10;
        }

        if (level >= 80)
        {
            mask |= 0x20;
        }

        return mask;
    }

    /// <summary>
    /// Writes the socket ids and the unlock mask for the character's level.
    /// </summary>
    /// <remarks>
    /// The socket ids come from <c>GlyphSlot.dbc</c> by their 1-based order. Without them the pane
    /// has no sockets to draw at all, unlock mask or not.
    /// </remarks>
    public void InitialiseForLevel(DbcStore<GlyphSlotEntry>? slots)
    {
        if (slots is not null)
        {
            foreach (GlyphSlotEntry slot in slots.Entries)
            {
                if (slot.Order is > 0 and <= SlotCount)
                {
                    owner.Fields.SetUInt32(
                        UpdateFields.PLAYER_FIELD_GLYPH_SLOTS_1 + (int)(slot.Order - 1), slot.Id);
                }
            }
        }

        owner.Fields.SetUInt32(UpdateFields.PLAYER_GLYPHS_ENABLED, EnabledMaskFor(owner.Level));
    }

    /// <summary>Whether a socket is unlocked at this character's level.</summary>
    public bool IsUnlocked(int slot) =>
        slot >= 0 && slot < SlotCount
        && (owner.Fields.GetUInt32(UpdateFields.PLAYER_GLYPHS_ENABLED) & (1u << slot)) != 0;

    /// <summary>The glyph in a socket of the spec being played.</summary>
    public uint Get(int slot) =>
        slot >= 0 && slot < SlotCount ? _bySpec[ActiveSpec][slot] : 0;

    /// <summary>
    /// Puts a glyph in a socket, or clears it with zero.
    /// </summary>
    /// <returns>False when the socket is locked, or the glyph does not fit it.</returns>
    /// <remarks>
    /// <b>Major glyphs do not fit minor sockets.</b> Both the glyph and the socket carry a type
    /// mask and they have to agree; the client enforces it, which is why the server must.
    /// </remarks>
    public bool Set(
        int slot,
        uint glyphId,
        DbcStore<GlyphPropertiesEntry>? glyphs,
        DbcStore<GlyphSlotEntry>? slots)
    {
        if (slot < 0 || slot >= SlotCount || !IsUnlocked(slot))
        {
            return false;
        }

        if (glyphId != 0)
        {
            if (glyphs is null || !glyphs.TryGet(glyphId, out GlyphPropertiesEntry? glyph)
                || glyph is null)
            {
                return false;
            }

            uint socketId = owner.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_GLYPH_SLOTS_1 + slot);

            if (slots is not null
                && slots.TryGet(socketId, out GlyphSlotEntry? socket)
                && socket is not null
                && socket.TypeFlags != glyph.TypeFlags)
            {
                return false;
            }
        }

        _bySpec[ActiveSpec][slot] = glyphId;
        owner.Fields.SetUInt32(UpdateFields.PLAYER_FIELD_GLYPHS_1 + slot, glyphId);

        return true;
    }

    /// <summary>Puts a saved glyph back, without checking anything.</summary>
    public void Restore(int spec, int slot, uint glyphId)
    {
        if (spec < 0 || spec >= PlayerTalents.MaxSpecs || slot < 0 || slot >= SlotCount)
        {
            return;
        }

        _bySpec[spec][slot] = glyphId;

        if (spec == ActiveSpec)
        {
            owner.Fields.SetUInt32(UpdateFields.PLAYER_FIELD_GLYPHS_1 + slot, glyphId);
        }
    }

    /// <summary>Rewrites the visible glyph fields from the spec being played.</summary>
    /// <remarks>
    /// Called after a spec switch. The fields hold one spec's worth, so switching without this
    /// leaves the other spec's glyphs on the pane and in effect.
    /// </remarks>
    public void RefreshFields()
    {
        for (int slot = 0; slot < SlotCount; slot++)
        {
            owner.Fields.SetUInt32(
                UpdateFields.PLAYER_FIELD_GLYPHS_1 + slot, _bySpec[ActiveSpec][slot]);
        }
    }

    private byte ActiveSpec => owner.Talents.ActiveSpec;
}
