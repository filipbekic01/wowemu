using WowEmu.Core;
using WowEmu.Data.Db;

namespace WowEmu.Game;

/// <summary>What a player chose in a loot roll. <c>RollVote</c>.</summary>
public enum LootVote : byte
{
    Pass = 0,
    Need = 1,
    Greed = 2,
    Disenchant = 3,

    /// <summary>Asked but not yet answered. <c>NOT_EMITED_YET</c>.</summary>
    Pending = 4,

    /// <summary>Never eligible — the item is not for them.</summary>
    NotValid = 5,
}

/// <summary>
/// Which roll buttons the client offers. <c>rollVoteMask</c>.
/// </summary>
/// <remarks>
/// A mask rather than a count: greed and disenchant are independent of need, and a player who
/// cannot use an item still gets the greed button.
/// </remarks>
public static class LootRollMask
{
    public const byte Pass = 0x01;
    public const byte Need = 0x02;
    public const byte Greed = 0x04;
    public const byte Disenchant = 0x08;

    /// <summary>Everything a usable item offers.</summary>
    public const byte All = Pass | Need | Greed | Disenchant;
}

/// <summary>One player's answer.</summary>
public readonly record struct LootRollVote(ObjectGuid Player, LootVote Vote, byte Roll);

/// <summary>How a roll ended.</summary>
public readonly record struct LootRollOutcome(
    bool EveryonePassed, ObjectGuid Winner, byte WinningRoll, LootVote WinningVote);

/// <summary>
/// One item under contest.
/// </summary>
/// <remarks>
/// Port of <c>Roll</c>. The roll belongs to the item rather than to the corpse: a body with three
/// contested drops runs three rolls at once, and the client keys its windows on the slot.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Guid is the client's own vocabulary for these; renaming would obscure the port.")]
public sealed class GroupLootRoll
{
    /// <summary>How long a roll runs before it is decided for everyone. Sixty seconds.</summary>
    public const uint TimeoutMs = 60_000;

    private readonly Dictionary<ObjectGuid, LootVote> _votes = [];

    /// <summary>Which loot this roll belongs to, and which slot of it.</summary>
    public required ObjectGuid Holder { get; init; }

    /// <inheritdoc cref="Holder"/>
    public required byte Slot { get; init; }

    public required uint ItemId { get; init; }
    public required uint Count { get; init; }

    /// <summary>The rolled suffix, so the roll window shows "of the Bear" like the corpse did.</summary>
    public int RandomPropertyId { get; init; }

    public uint SuffixFactor { get; init; }

    /// <summary>Which buttons each player was offered.</summary>
    public byte VoteMask { get; init; } = LootRollMask.All;

    /// <summary>How much longer the roll runs.</summary>
    public uint RemainingMs { get; private set; } = TimeoutMs;

    /// <summary>Everyone who was asked, and what they said.</summary>
    public IReadOnlyDictionary<ObjectGuid, LootVote> Votes => _votes;

    /// <summary>Adds a candidate, not yet answered.</summary>
    public void Ask(ObjectGuid player) => _votes[player] = LootVote.Pending;

    /// <summary>
    /// Records an answer.
    /// </summary>
    /// <returns>False when they were not asked, or have already answered.</returns>
    /// <remarks>
    /// <b>One answer each.</b> Without the check a client can send Need repeatedly and roll as
    /// many times as it likes, taking the best of them.
    /// </remarks>
    public bool Vote(ObjectGuid player, LootVote vote)
    {
        if (!_votes.TryGetValue(player, out LootVote current) || current != LootVote.Pending)
        {
            return false;
        }

        _votes[player] = vote;

        return true;
    }

    /// <summary>Whether everyone asked has answered.</summary>
    public bool IsSettled
    {
        get
        {
            foreach (LootVote vote in _votes.Values)
            {
                if (vote == LootVote.Pending)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Advances the timer.</summary>
    /// <returns>Whether the roll has run out.</returns>
    public bool Tick(uint diff)
    {
        RemainingMs = RemainingMs > diff ? RemainingMs - diff : 0;

        return RemainingMs == 0;
    }

    /// <summary>
    /// Decides the roll.
    /// </summary>
    /// <param name="roll">A draw over [1, 100], as upstream's <c>urand(1, 100)</c>.</param>
    /// <param name="rolled">Each participant's roll, for the client's log.</param>
    /// <remarks>
    /// <b>Need beats greed outright.</b> A need roll of 1 takes the item from a greed roll of 100 —
    /// the two are not compared against each other, and a single highest-roll pass over both is the
    /// obvious implementation and completely wrong.
    /// <para>
    /// Anyone still Pending when the timer runs out counts as a pass, which is what makes a roll
    /// terminate when somebody walks away from their keyboard.
    /// </para>
    /// </remarks>
    public LootRollOutcome Decide(Func<byte> roll, out IReadOnlyList<LootRollVote> rolled)
    {
        ArgumentNullException.ThrowIfNull(roll);

        List<LootRollVote> log = [];
        rolled = log;

        // Need first, and only if somebody needed. Greed is not consulted at all in that case.
        if (Best(LootVote.Need, roll, log) is { } needWinner)
        {
            return needWinner;
        }

        // Greed and disenchant roll together — both are "I do not need this", and upstream puts
        // them in one pool rather than ranking one above the other.
        if (Best(LootVote.Greed, roll, log) is { } greedWinner)
        {
            return greedWinner;
        }

        return new LootRollOutcome(true, ObjectGuid.Empty, 0, LootVote.Pass);
    }

    private LootRollOutcome? Best(LootVote kind, Func<byte> roll, List<LootRollVote> log)
    {
        byte best = 0;
        ObjectGuid winner = ObjectGuid.Empty;
        LootVote winningVote = kind;

        foreach ((ObjectGuid player, LootVote vote) in _votes)
        {
            bool counts = kind == LootVote.Need
                ? vote == LootVote.Need
                : vote is LootVote.Greed or LootVote.Disenchant;

            if (!counts)
            {
                continue;
            }

            byte value = roll();

            log.Add(new LootRollVote(player, vote, value));

            // Strictly greater, so the first of a tie wins rather than the last. Upstream does the
            // same; either is arbitrary, but they have to agree or a re-run picks differently.
            if (value > best)
            {
                best = value;
                winner = player;
                winningVote = vote;
            }
        }

        return winner.IsEmpty ? null : new LootRollOutcome(false, winner, best, winningVote);
    }
}

/// <summary>
/// The loot rules a group's method implies.
/// </summary>
/// <remarks>
/// Port of <c>Group::GroupLoot</c>, <c>NeedBeforeGreed</c>, <c>MasterLoot</c> and the round-robin
/// handling in <c>Player::SendLoot</c>.
/// </remarks>
public static class GroupLoot
{
    /// <summary>
    /// Whether an item is contested rather than simply taken.
    /// </summary>
    /// <remarks>
    /// <b>Only at or above the group's threshold.</b> Rolling for every grey vendor trash makes a
    /// dungeon unplayable, which is exactly what the threshold exists to prevent.
    /// </remarks>
    public static bool NeedsRoll(byte lootMethod, byte quality, byte threshold) =>
        lootMethod is LootMethod.GroupLoot or LootMethod.NeedBeforeGreed && quality >= threshold;

    /// <summary>
    /// Whether an item above the threshold is the master looter's to hand out.
    /// </summary>
    public static bool IsMasterLooted(byte lootMethod, byte quality, byte threshold) =>
        lootMethod == LootMethod.MasterLoot && quality >= threshold;

    /// <summary>
    /// Which buttons a player is offered for an item.
    /// </summary>
    /// <remarks>
    /// <b>Need is offered only to someone who can use the item.</b> Under need-before-greed the
    /// client hides the button; under group loot everyone gets it. Offering need to a player whose
    /// class cannot equip the item is how a plate wearer takes a cloth robe.
    /// </remarks>
    public static byte VoteMaskFor(byte lootMethod, bool canUse) =>
        lootMethod == LootMethod.NeedBeforeGreed && !canUse
            ? (byte)(LootRollMask.Pass | LootRollMask.Greed | LootRollMask.Disenchant)
            : LootRollMask.All;

    /// <summary>
    /// Whether a player may take an uncontested item from a corpse.
    /// </summary>
    /// <remarks>
    /// <b>Round-robin is the only method that restricts ordinary drops.</b> Under free-for-all,
    /// group loot and need-before-greed anyone in the group may take what is not rolled for; under
    /// master loot only items above the threshold are restricted. Applying the round-robin turn to
    /// every method leaves four members watching one person loot everything.
    /// </remarks>
    public static bool CanTakeUncontested(Group? group, ObjectGuid player, ObjectGuid looter) =>
        group is null
        || group.LootMethod != LootMethod.RoundRobin
        || looter.IsEmpty
        || looter == player;

    /// <summary>
    /// Whether a player can use an item, for the need button.
    /// </summary>
    /// <remarks>
    /// Class and race masks only. A full usability check needs the proficiency and level rules the
    /// inventory already owns; this is the subset the roll window depends on, and it is deliberately
    /// permissive — a need roll a player should not have had is a smaller wrong than a button
    /// missing from someone who should.
    /// </remarks>
    public static bool CanUse(ItemTemplate template, byte classId, byte race)
    {
        ArgumentNullException.ThrowIfNull(template);

        int classMask = classId == 0 ? 0 : 1 << (classId - 1);
        int raceMask = race == 0 ? 0 : 1 << (race - 1);

        // Zero means "anyone", not "nobody" — the common case for almost every item in the game.
        // The columns are signed, and -1 is the other spelling of "anyone": read unsigned it is
        // four billion, which happens to pass, but only by accident.
        return (template.AllowableClass is 0 or -1 || (template.AllowableClass & classMask) != 0)
            && (template.AllowableRace is 0 or -1 || (template.AllowableRace & raceMask) != 0);
    }
}
