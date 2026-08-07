using WowEmu.Data.Client;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// What a player is trained in, and how good at it they are.
/// </summary>
/// <remarks>
/// Port of <c>Player::SetSkill</c> and its accessors. The client keeps this entirely in update
/// fields: 127 slots of three words each, starting at <c>PLAYER_SKILL_INFO_1_1</c>. There is no
/// packet — the skill window is drawn from the fields, so writing them correctly is the whole of
/// making skills appear.
/// <para>
/// <b>A slot's position is not the skill id.</b> Skills occupy the first free slot in the order
/// they are learned, and nothing sorts them afterwards, so the mapping from skill to slot has to be
/// remembered separately. It is also why removing a skill clears its fields rather than compacting:
/// moving a later skill down would change the slot every other reference is holding.
/// </para>
/// </remarks>
public sealed class PlayerSkills(Player owner)
{
    /// <summary>How many skills a character can hold at once. <c>PLAYER_MAX_SKILLS</c>.</summary>
    /// <remarks>
    /// 127, not 128. The field block has room for 128 triples, and upstream uses 127 — the last
    /// slot is left alone.
    /// </remarks>
    public const int MaxSkills = 127;

    /// <summary>Which slot each known skill sits in. Upstream's <c>mSkillStatus</c>.</summary>
    private readonly Dictionary<uint, int> _slots = [];

    /// <summary>Every skill the player knows, with no ordering promised.</summary>
    public IEnumerable<uint> Known => _slots.Keys;

    /// <summary>How many skills are known.</summary>
    public int Count => _slots.Count;

    /// <summary>Whether the player has a skill at all, at any value.</summary>
    public bool Has(uint skillId) => skillId != 0 && _slots.ContainsKey(skillId);

    /// <summary>
    /// The value the client shows and everything else asks for — base plus both bonuses.
    /// </summary>
    /// <remarks>
    /// Port of <c>GetSkillValue</c>. Clamped at zero rather than allowed to go negative: the
    /// temporary bonus can be a penalty, and a negative skill would read as an enormous one once it
    /// went back through the unsigned field.
    /// </remarks>
    public ushort Value(uint skillId)
    {
        if (!_slots.TryGetValue(skillId, out int slot))
        {
            return 0;
        }

        int result = Low(ValueIndex(slot)) + TemporaryBonus(skillId) + PermanentBonus(skillId);

        return result < 0 ? (ushort)0 : (ushort)result;
    }

    /// <summary>The value with no bonuses at all — what is actually stored.</summary>
    /// <remarks>
    /// Port of <c>GetPureSkillValue</c>. This is the one that gets saved and the one a skill-up
    /// raises; using <see cref="Value"/> for either would bake a temporary buff into the character.
    /// </remarks>
    public ushort PureValue(uint skillId) =>
        _slots.TryGetValue(skillId, out int slot) ? (ushort)Low(ValueIndex(slot)) : (ushort)0;

    /// <summary>The value plus only the permanent bonus. <c>GetBaseSkillValue</c>.</summary>
    public ushort BaseValue(uint skillId)
    {
        if (!_slots.TryGetValue(skillId, out int slot))
        {
            return 0;
        }

        int result = Low(ValueIndex(slot)) + PermanentBonus(skillId);

        return result < 0 ? (ushort)0 : (ushort)result;
    }

    /// <summary>The ceiling the client draws the bar against, bonuses included.</summary>
    public ushort MaxValue(uint skillId)
    {
        if (!_slots.TryGetValue(skillId, out int slot))
        {
            return 0;
        }

        int result = High(ValueIndex(slot)) + TemporaryBonus(skillId) + PermanentBonus(skillId);

        return result < 0 ? (ushort)0 : (ushort)result;
    }

    /// <summary>The stored ceiling, with no bonuses. <c>GetPureMaxSkillValue</c>.</summary>
    public ushort PureMaxValue(uint skillId) =>
        _slots.TryGetValue(skillId, out int slot) ? (ushort)High(ValueIndex(slot)) : (ushort)0;

    /// <summary>Which tier a ranked skill has reached — 1 is apprentice. Zero for everything else.</summary>
    public ushort Step(uint skillId) =>
        _slots.TryGetValue(skillId, out int slot) ? (ushort)High(SkillIndex(slot)) : (ushort)0;

    /// <summary>
    /// The bonus that outlives a logout — enchantments, and the profession books.
    /// </summary>
    /// <remarks>
    /// <b>Signed, and stored in the HIGH half.</b> The temporary bonus takes the low half, which is
    /// the opposite way round from every other paired field in the block, so it is worth reading
    /// <c>SKILL_PERM_BONUS</c> before assuming.
    /// </remarks>
    public short PermanentBonus(uint skillId) =>
        _slots.TryGetValue(skillId, out int slot) ? (short)High(BonusIndex(slot)) : (short)0;

    /// <summary>The bonus from an aura, which can be a penalty. Stored in the LOW half.</summary>
    public short TemporaryBonus(uint skillId) =>
        _slots.TryGetValue(skillId, out int slot) ? (short)Low(BonusIndex(slot)) : (short)0;

    /// <summary>Sets both bonuses at once, since they share a word.</summary>
    public void SetBonus(uint skillId, short temporary, short permanent)
    {
        if (_slots.TryGetValue(skillId, out int slot))
        {
            owner.Fields.SetUInt32(BonusIndex(slot), Pair((ushort)temporary, (ushort)permanent));
        }
    }

    /// <summary>
    /// Learns a skill, changes its value, or — with a value of zero — forgets it.
    /// </summary>
    /// <remarks>
    /// Port of <c>Player::SetSkill</c>, whose three behaviours all hang off this one signature.
    /// A value of zero for a known skill is a removal; for an unknown skill it is a no-op rather
    /// than an empty slot, which is what stops "clear this skill" from consuming one of the 127.
    /// </remarks>
    /// <returns>False when the skill could not be placed — the block is full.</returns>
    public bool Set(uint skillId, ushort step, ushort value, ushort maxValue)
    {
        if (skillId == 0)
        {
            return false;
        }

        if (_slots.TryGetValue(skillId, out int slot))
        {
            if (value == 0)
            {
                Forget(skillId, slot);
                return true;
            }

            Write(slot, skillId, step, value, maxValue);
            return true;
        }

        if (value == 0)
        {
            return false;
        }

        if (FirstFreeSlot() is not { } free)
        {
            return false;
        }

        _slots[skillId] = free;
        Write(free, skillId, step, value, maxValue);

        return true;
    }

    /// <summary>Forgets a skill, clearing all three of its words.</summary>
    /// <remarks>
    /// The bonus word has to go too. Leaving it behind means the next skill to take this slot is
    /// born with somebody else's enchantment on it.
    /// </remarks>
    private void Forget(uint skillId, int slot)
    {
        owner.Fields.SetUInt32(SkillIndex(slot), 0);
        owner.Fields.SetUInt32(ValueIndex(slot), 0);
        owner.Fields.SetUInt32(BonusIndex(slot), 0);

        _slots.Remove(skillId);
    }

    private void Write(int slot, uint skillId, ushort step, ushort value, ushort maxValue)
    {
        owner.Fields.SetUInt32(SkillIndex(slot), Pair((ushort)skillId, step));
        owner.Fields.SetUInt32(ValueIndex(slot), Pair(value, maxValue));
    }

    /// <summary>
    /// The first slot with no skill id in it.
    /// </summary>
    /// <remarks>
    /// Reads the field rather than the dictionary on purpose: the two are meant to agree, and if
    /// they ever do not, a slot that looks free to the dictionary but holds a skill id would be
    /// overwritten — silently losing a skill the client is already drawing.
    /// </remarks>
    private int? FirstFreeSlot()
    {
        for (int slot = 0; slot < MaxSkills; slot++)
        {
            if (owner.Fields.GetUInt32(SkillIndex(slot)) == 0)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>Every skill as it should be written out, in no particular order.</summary>
    /// <remarks>
    /// The stored value, not <see cref="Value"/> — saving the bonused figure would fold a temporary
    /// buff into the character permanently, a point per logout for as long as it was up.
    /// </remarks>
    public IEnumerable<(ushort Skill, ushort Value, ushort Max, ushort Step)> Snapshot()
    {
        foreach (uint skillId in _slots.Keys)
        {
            yield return ((ushort)skillId, PureValue(skillId), PureMaxValue(skillId), Step(skillId));
        }
    }

    /// <summary>Puts back what was saved, replacing whatever is there.</summary>
    public void Restore(IEnumerable<(ushort Skill, ushort Value, ushort Max, ushort Step)> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);

        foreach (uint skillId in _slots.Keys.ToArray())
        {
            Forget(skillId, _slots[skillId]);
        }

        foreach ((ushort skill, ushort value, ushort max, ushort step) in saved)
        {
            Set(skill, step, value, max);
        }
    }

    private static int SkillIndex(int slot) => UpdateFields.PLAYER_SKILL_INFO_1_1 + (slot * 3);

    private static int ValueIndex(int slot) => SkillIndex(slot) + 1;

    private static int BonusIndex(int slot) => SkillIndex(slot) + 2;

    private int Low(int index) => (ushort)owner.Fields.GetUInt32(index);

    private int High(int index) => (ushort)(owner.Fields.GetUInt32(index) >> 16);

    /// <summary>Two shorts in one word, low first. <c>MAKE_PAIR32</c>.</summary>
    private static uint Pair(ushort low, ushort high) => low | ((uint)high << 16);
}
