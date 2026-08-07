using WowEmu.Data.Db;

namespace WowEmu.Game;

/// <summary>
/// Every spell a player knows.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Player</c>'s <c>m_spells</c> that M6 needs: what goes in it at creation,
/// what a trainer adds, and which ranks are current. No talents and no specs.
/// <para>
/// <b>A superseded rank is deactivated, not removed.</b> Upstream keeps every rank in the book and
/// flips the lower one inactive, and the client is told to swap it on the action bar. Removing it
/// instead loses the fact that the character ever had it — which matters the moment a spec change
/// or an unlearn has to put it back.
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

    /// <summary>Every spell known, and whether it is the current rank.</summary>
    private readonly Dictionary<uint, bool> _known = [];

    /// <summary>
    /// The chains, so a higher rank can supersede a lower one. Null until content is loaded.
    /// </summary>
    /// <remarks>
    /// Settable rather than required: nothing else about a spellbook needs a database, and a test
    /// that had to load one to learn a spell would not get written. With none set, every rank stays
    /// active — which is what happened before this existed.
    /// </remarks>
    public SpellRankStore? Ranks { get; set; }

    /// <summary>Every spell known, in no particular order — including superseded ranks.</summary>
    public IReadOnlyCollection<uint> Known => _known.Keys;

    /// <summary>Only the ranks a player can actually cast.</summary>
    public IEnumerable<uint> Active
    {
        get
        {
            foreach ((uint spellId, bool active) in _known)
            {
                if (active)
                {
                    yield return spellId;
                }
            }
        }
    }

    public int Count => _known.Count;

    public bool Knows(uint spellId) => _known.ContainsKey(spellId);

    /// <summary>Whether a known spell is the current rank rather than one that has been outgrown.</summary>
    public bool IsActive(uint spellId) => _known.TryGetValue(spellId, out bool active) && active;

    /// <summary>
    /// Adds a spell to the book.
    /// </summary>
    /// <returns><c>false</c> if it was already known, so the caller can stay quiet about it.</returns>
    public bool Learn(uint spellId)
    {
        if (_known.ContainsKey(spellId))
        {
            return false;
        }

        _known[spellId] = true;

        Supersede(spellId);
        ApplyPassiveEffects(spellId);

        return true;
    }

    /// <summary>
    /// Settles which rank of a chain is the live one after learning <paramref name="learned"/>.
    /// </summary>
    /// <returns>The rank this one replaced, or 0 — which is what the client needs to swap a bar.</returns>
    /// <remarks>
    /// It runs in both directions, and the second is the one that is easy to miss: learning a
    /// <i>lower</i> rank than one already known must deactivate the <b>new</b> spell rather than the
    /// old one. That happens whenever a trainer's list is worked through out of order, and getting
    /// it backwards silently downgrades the character.
    /// </remarks>
    public uint Supersede(uint learned)
    {
        if (Ranks is not { } ranks || !ranks.IsRanked(learned))
        {
            return 0;
        }

        uint replaced = 0;

        foreach (uint other in ranks.ChainOf(learned))
        {
            if (other == learned || !_known.TryGetValue(other, out bool active) || !active)
            {
                continue;
            }

            if (ranks.Supersedes(learned, other))
            {
                _known[other] = false;
                replaced = other;
            }
            else if (ranks.Supersedes(other, learned))
            {
                // Already have better. The new one goes in the book inactive, so it is remembered
                // without being offered.
                _known[learned] = false;
            }
        }

        return replaced;
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
            _known[spellId] = true;
        }

        // After the whole set is in, not per spell: the ranks arrive in whatever order the database
        // returns them, and settling each as it lands would deactivate a high rank that happens to
        // be read before its own lower ones.
        foreach (uint spellId in _known.Keys.ToArray())
        {
            Supersede(spellId);
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
            owner.CanDualWield = _known.ContainsKey(DualWieldSpell);
        }
    }
}
