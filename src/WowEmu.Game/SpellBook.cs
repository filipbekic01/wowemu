namespace WowEmu.Game;

/// <summary>
/// Every spell a player knows.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Player</c>'s <c>m_spells</c> that M6 needs: a set, what goes in it at
/// creation, and what a trainer adds. No talents, no specs, no ranks and no skill lines — a higher
/// rank does not supersede a lower one here, so a player who learns rank 2 keeps rank 1 in the
/// book as well.
/// <para>
/// It is a set rather than a list because the client is told about each spell once and the only
/// question ever asked of it is whether a spell is in there.
/// </para>
/// </remarks>
public sealed class SpellBook(Player owner)
{
    /// <summary>
    /// Dual Wield. Knowing it is the whole of what lets a player hold a weapon in each hand.
    /// </summary>
    /// <remarks>
    /// Rogues and death knights start with it — classmask 40 in
    /// <c>playercreateinfo_spell</c> — and a warrior learns it from a trainer at level 20. It is
    /// deliberately not a class trait: making it one gives every level-1 warrior an off-hand.
    /// </remarks>
    public const uint DualWieldSpell = 674;

    private readonly HashSet<uint> _known = [];

    /// <summary>Every spell known, in no particular order.</summary>
    public IReadOnlyCollection<uint> Known => _known;

    public int Count => _known.Count;

    public bool Knows(uint spellId) => _known.Contains(spellId);

    /// <summary>
    /// Adds a spell to the book.
    /// </summary>
    /// <returns><c>false</c> if it was already known, so the caller can stay quiet about it.</returns>
    public bool Learn(uint spellId)
    {
        if (!_known.Add(spellId))
        {
            return false;
        }

        ApplyPassiveEffects(spellId);

        return true;
    }

    /// <summary>Takes a spell out of the book.</summary>
    public bool Forget(uint spellId)
    {
        if (!_known.Remove(spellId))
        {
            return false;
        }

        ApplyPassiveEffects(spellId);

        return true;
    }

    /// <summary>Fills the book from a saved set, without announcing anything.</summary>
    /// <remarks>
    /// For loading. The client is told the whole book at once by <c>SMSG_INITIAL_SPELLS</c> rather
    /// than one spell at a time, so a learn packet per spell on login would be several hundred
    /// packets saying what one already said.
    /// </remarks>
    public void Restore(IEnumerable<uint> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);

        foreach (uint spellId in spells)
        {
            _known.Add(spellId);
        }

        ApplyPassiveEffects(DualWieldSpell);
    }

    /// <summary>
    /// Re-derives the things that are simply a consequence of knowing a spell.
    /// </summary>
    /// <remarks>
    /// Recomputed from the book rather than toggled on learn, for the same reason equipment stats
    /// are recomputed rather than adjusted: a toggle is one missed call away from a character who
    /// can dual wield because they once could.
    /// </remarks>
    private void ApplyPassiveEffects(uint spellId)
    {
        if (spellId == DualWieldSpell)
        {
            owner.CanDualWield = _known.Contains(DualWieldSpell);
        }
    }
}
