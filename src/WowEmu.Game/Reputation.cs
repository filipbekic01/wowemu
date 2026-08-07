using WowEmu.Data.Client;

namespace WowEmu.Game;

/// <summary>How a faction feels about you. <c>ReputationRank</c>.</summary>
public static class ReputationRank
{
    public const byte Hated = 0;
    public const byte Hostile = 1;
    public const byte Unfriendly = 2;
    public const byte Neutral = 3;
    public const byte Friendly = 4;
    public const byte Honored = 5;
    public const byte Revered = 6;
    public const byte Exalted = 7;

    /// <summary>How many there are. <c>MAX_REPUTATION_RANK</c>.</summary>
    public const int Count = 8;
}

/// <summary>
/// What every faction thinks of one character.
/// </summary>
/// <remarks>
/// Port of <c>ReputationMgr</c>. Standing reaches the client as its own packet, keyed by a faction's
/// <i>reputation list id</i> rather than its faction id — <b>most factions have no list id at all</b>
/// and are tracked server-side without ever being shown.
/// </remarks>
public sealed class Reputation(Player owner)
{
    /// <summary>
    /// How much standing each rank spans. <c>PointsInRank</c>.
    /// </summary>
    /// <remarks>
    /// <b>Wildly uneven, and deliberately so.</b> Hated spans 36,000 and Exalted only 1,000 — the
    /// bottom is a long way down and the top is a threshold you cross. Treating them as equal bands
    /// puts every rank boundary in the wrong place.
    /// </remarks>
    public static readonly int[] PointsInRank = [36000, 3000, 3000, 3000, 6000, 12000, 21000, 1000];

    /// <summary>The most standing a faction can hold, and the least.</summary>
    public const int Cap = 42999;
    public const int Bottom = -42000;

    /// <summary>How many factions the client can show. <c>MAX_REPUTATION_INDEX</c>.</summary>
    public const int MaxIndex = 128;

    /// <summary>Standing by faction id, for every faction this character has met.</summary>
    private readonly Dictionary<uint, int> _standing = [];

    /// <summary>Every faction with a standing, in no particular order.</summary>
    public IReadOnlyDictionary<uint, int> All => _standing;

    /// <summary>
    /// Which rank a standing falls in.
    /// </summary>
    /// <remarks>
    /// Counted down from the cap rather than up from the bottom, which is upstream's own shape and
    /// the one that reads correctly: each rank's ceiling is the previous one's floor, and starting
    /// from the bottom accumulates the uneven spans in the wrong direction.
    /// </remarks>
    public static byte RankOf(int standing)
    {
        int limit = Cap + 1;

        for (int rank = ReputationRank.Count - 1; rank >= 0; rank--)
        {
            limit -= PointsInRank[rank];

            if (standing >= limit)
            {
                return (byte)rank;
            }
        }

        return ReputationRank.Hated;
    }

    /// <summary>The lowest standing that counts as a rank.</summary>
    public static int StandingFor(byte rank)
    {
        int standing = Bottom;

        for (int i = 0; i <= rank && i < ReputationRank.Count; i++)
        {
            standing += PointsInRank[i];
        }

        return Math.Max(standing - 1, Bottom);
    }

    /// <summary>What this character's standing with a faction is.</summary>
    /// <remarks>
    /// A faction never met sits at the bottom of Neutral, not at zero-meaning-nothing: Neutral is
    /// where everyone starts, and it is a real standing rather than an absence.
    /// </remarks>
    public int StandingWith(uint factionId) =>
        _standing.TryGetValue(factionId, out int standing) ? standing : 0;

    /// <summary>What rank this character is at with a faction.</summary>
    public byte RankWith(uint factionId) => RankOf(StandingWith(factionId));

    /// <summary>Whether the character is at least a given rank.</summary>
    public bool IsAtLeast(uint factionId, byte rank) => RankWith(factionId) >= rank;

    /// <summary>
    /// Changes standing, and tells the client if the faction is one it can show.
    /// </summary>
    /// <returns>The rank afterwards.</returns>
    /// <remarks>
    /// Clamped at both ends. Standing is signed and the bottom is -42,000, so an unclamped
    /// subtraction runs off into a rank that does not exist.
    /// </remarks>
    public byte Gain(uint factionId, int amount, DbcStore<FactionEntry>? factions = null)
    {
        int updated = Math.Clamp(StandingWith(factionId) + amount, Bottom, Cap);

        _standing[factionId] = updated;

        Announce(factionId, updated, factions);

        return RankOf(updated);
    }

    /// <summary>Sets standing outright, for loading a saved character.</summary>
    public void Set(uint factionId, int standing, DbcStore<FactionEntry>? factions = null)
    {
        int clamped = Math.Clamp(standing, Bottom, Cap);

        _standing[factionId] = clamped;

        Announce(factionId, clamped, factions);
    }

    /// <summary>
    /// What a vendor or trainer of this faction charges, as a multiplier.
    /// </summary>
    /// <param name="factionId">
    /// The faction itself, not the faction <i>template</i> a creature carries. A template with no
    /// faction behind it gives no discount, which is most of the neutral world.
    /// </param>
    /// <remarks>
    /// Port of <c>Player::GetReputationPriceDiscount</c>. Five percent per rank above Neutral, so an
    /// Exalted character pays 80% — and <b>nothing below Friendly</b>. Being disliked does not make
    /// things more expensive; the multiplier never goes above 1.0.
    /// </remarks>
    public float PriceDiscount(uint factionId)
    {
        if (factionId == 0)
        {
            return 1.0f;
        }

        byte rank = RankWith(factionId);

        return rank <= ReputationRank.Neutral
            ? 1.0f
            : 1.0f - (0.05f * (rank - ReputationRank.Neutral));
    }

    /// <summary>
    /// Tells the client a faction's standing changed.
    /// </summary>
    /// <remarks>
    /// <b>Reputation is not an update field in 3.3.5.</b> It travels as its own packet, keyed by the
    /// faction's <i>reputation list id</i> rather than its faction id — and most factions have no
    /// list id, so they are tracked server-side and never shown. Looking for a field block to write
    /// is the wrong search entirely; there is none.
    /// </remarks>
    private void Announce(uint factionId, int standing, DbcStore<FactionEntry>? factions)
    {
        if (factions is null || !factions.TryGet(factionId, out FactionEntry? faction) || faction is null)
        {
            return;
        }

        if (faction.ReputationListId < 0 || faction.ReputationListId >= MaxIndex)
        {
            return;
        }

        owner.Connection?.SendFactionStanding((uint)faction.ReputationListId, standing);
    }
}
