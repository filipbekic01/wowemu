using WowEmu.Data.Client;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// Which titles a character has earned, and which one is worn.
/// </summary>
/// <remarks>
/// Port of <c>Player::SetTitle</c> and <c>Player::SetCurrentTitle</c>. Both halves live in update
/// fields: a 192-bit mask of what is known, and one integer for what is displayed.
/// <para>
/// <b>Everything here is keyed by a title's <i>bit index</i>, not its id.</b> A quest names the id
/// from <c>CharTitles.dbc</c>; the bit index is a separate column of the same row. They happen to
/// agree for the first few dozen rows, which is exactly why using the id looks fine right up until
/// it silently grants the wrong title.
/// </para>
/// </remarks>
public sealed class PlayerTitles(Player owner)
{
    /// <summary>
    /// How many titles the mask can hold. <c>MAX_TITLE_INDEX</c>.
    /// </summary>
    /// <remarks>
    /// <b>Three <i>uint64</i> fields, so 192 — not the 128 the four-dword reading suggests.</b>
    /// <c>CharTitles.dbc</c> carries bit indices up to 142, and a 128-bit mask silently drops the
    /// fifteen titles above it: they read as unearned forever, with nothing to say so.
    /// </remarks>
    public const int Count = 192;

    /// <summary>How many bits one field holds.</summary>
    private const int BitsPerField = 32;

    /// <summary>
    /// Marks a title known, by bit index.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public bool LearnByBit(uint bitIndex)
    {
        if (bitIndex >= Count)
        {
            return false;
        }

        int field = UpdateFields.PLAYER__FIELD_KNOWN_TITLES + (int)(bitIndex / BitsPerField);
        uint flag = 1u << (int)(bitIndex % BitsPerField);
        uint current = owner.Fields.GetUInt32(field);

        if ((current & flag) != 0)
        {
            return false;
        }

        owner.Fields.SetUInt32(field, current | flag);

        return true;
    }

    /// <summary>Whether a title is known, by bit index.</summary>
    public bool HasByBit(uint bitIndex)
    {
        if (bitIndex >= Count)
        {
            return false;
        }

        int field = UpdateFields.PLAYER__FIELD_KNOWN_TITLES + (int)(bitIndex / BitsPerField);

        return (owner.Fields.GetUInt32(field) & (1u << (int)(bitIndex % BitsPerField))) != 0;
    }

    /// <summary>
    /// Marks a title known, by <c>CharTitles.dbc</c> id.
    /// </summary>
    /// <returns>Whether anything changed. False for an id the table does not carry.</returns>
    /// <remarks>
    /// The lookup is the whole point: it is what turns an id into the bit the client reads.
    /// </remarks>
    public bool Learn(uint titleId, DbcStore<CharTitleEntry>? titles)
    {
        if (titleId == 0 || titles is null)
        {
            return false;
        }

        return titles.TryGet(titleId, out CharTitleEntry? title)
            && title is not null
            && LearnByBit(title.BitIndex);
    }

    /// <summary>Takes a title away, by bit index.</summary>
    /// <remarks>
    /// Clears the worn title too when it is the one being removed — leaving it set displays a title
    /// the character no longer has, and the client has no reason to doubt the field.
    /// </remarks>
    public bool Remove(uint bitIndex)
    {
        if (!HasByBit(bitIndex))
        {
            return false;
        }

        int field = UpdateFields.PLAYER__FIELD_KNOWN_TITLES + (int)(bitIndex / BitsPerField);
        uint flag = 1u << (int)(bitIndex % BitsPerField);

        owner.Fields.SetUInt32(field, owner.Fields.GetUInt32(field) & ~flag);

        if (Chosen == bitIndex)
        {
            Chosen = 0;
        }

        return true;
    }

    /// <summary>
    /// The title being worn, as a bit index. Zero for none.
    /// </summary>
    /// <remarks>
    /// <b>Also the bit index, not the id</b> — the same trap as the mask, in a field whose name
    /// suggests otherwise.
    /// </remarks>
    public uint Chosen
    {
        get => owner.Fields.GetUInt32(UpdateFields.PLAYER_CHOSEN_TITLE);
        set => owner.Fields.SetUInt32(UpdateFields.PLAYER_CHOSEN_TITLE, value);
    }

    /// <summary>
    /// Wears a title, refusing any this character has not earned.
    /// </summary>
    /// <returns>Whether it was put on.</returns>
    /// <remarks>
    /// Port of <c>WorldSession::HandleSetTitleOpcode</c>, which checks the mask before setting the
    /// field. The client sends whatever the player clicked, and a modified one sends anything.
    /// </remarks>
    public bool Wear(uint bitIndex)
    {
        // Zero is "no title", and is always allowed — it is how a title is taken off.
        if (bitIndex == 0)
        {
            Chosen = 0;

            return true;
        }

        if (!HasByBit(bitIndex))
        {
            return false;
        }

        Chosen = bitIndex;

        return true;
    }

    /// <summary>Every bit index this character has earned, for saving.</summary>
    public IEnumerable<uint> Known
    {
        get
        {
            for (uint bit = 0; bit < Count; bit++)
            {
                if (HasByBit(bit))
                {
                    yield return bit;
                }
            }
        }
    }
}
