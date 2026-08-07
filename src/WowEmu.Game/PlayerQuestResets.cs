using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// Which repeating quests a character has already done this day, week or month.
/// </summary>
/// <remarks>
/// Port of <c>Player::SetDailyQuestStatus</c> and its weekly, monthly and reset siblings. Daily and
/// weekly quests are marked <c>Rewarded</c> like any other, so without this a character does one
/// and never sees it again — the record of having done it has to expire, and expire on a schedule
/// the whole server shares.
/// <para>
/// <b>Dailies live in update fields; weeklies and monthlies do not.</b> The client draws its own
/// "N dailies remaining" from a 25-slot block it can see, so dailies are capped at 25 and tracked
/// there. Weekly and monthly are server-side sets with no limit, and putting them in the block
/// would eat the daily allowance.
/// </para>
/// </remarks>
public sealed class PlayerQuestResets(Player owner)
{
    /// <summary>How many dailies a character may do in a day. <c>PLAYER_MAX_DAILY_QUESTS</c>.</summary>
    public const int MaxDaily = 25;

    private readonly HashSet<uint> _weekly = [];
    private readonly HashSet<uint> _monthly = [];

    /// <summary>Every weekly quest done since the last weekly reset.</summary>
    public IReadOnlyCollection<uint> Weekly => _weekly;

    /// <summary>Every monthly quest done since the last monthly reset.</summary>
    public IReadOnlyCollection<uint> Monthly => _monthly;

    /// <summary>Every daily quest done since the last daily reset, in slot order.</summary>
    public IEnumerable<uint> Daily
    {
        get
        {
            for (int slot = 0; slot < MaxDaily; slot++)
            {
                uint id = owner.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_DAILY_QUESTS_1 + slot);

                if (id != 0)
                {
                    yield return id;
                }
            }
        }
    }

    /// <summary>
    /// Records a quest as done, in whichever bucket it belongs to.
    /// </summary>
    /// <returns>Whether it was recorded. False for a quest that does not repeat on a schedule.</returns>
    /// <remarks>
    /// Checked in the order daily, weekly, monthly, and a quest lands in exactly one — the flags
    /// are separate columns and a quest that set two would otherwise be counted twice.
    /// </remarks>
    public bool Record(QuestTemplate quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        if (quest.IsDaily)
        {
            return RecordDaily(quest.Id);
        }

        if (quest.IsWeekly)
        {
            return _weekly.Add(quest.Id);
        }

        if (quest.IsMonthly)
        {
            return _monthly.Add(quest.Id);
        }

        return false;
    }

    /// <summary>Puts a daily in the first free slot.</summary>
    /// <remarks>
    /// <b>Silently does nothing when all 25 are taken.</b> That is upstream's behaviour, and it is
    /// the reason the allowance is checked before the quest is handed in rather than after.
    /// </remarks>
    private bool RecordDaily(uint questId)
    {
        for (int slot = 0; slot < MaxDaily; slot++)
        {
            int field = UpdateFields.PLAYER_FIELD_DAILY_QUESTS_1 + slot;

            if (owner.Fields.GetUInt32(field) == 0)
            {
                owner.Fields.SetUInt32(field, questId);

                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this character has already done a quest within its current period.</summary>
    public bool IsDone(QuestTemplate quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        if (quest.IsDaily)
        {
            return Daily.Contains(quest.Id);
        }

        if (quest.IsWeekly)
        {
            return _weekly.Contains(quest.Id);
        }

        return quest.IsMonthly && _monthly.Contains(quest.Id);
    }

    /// <summary>Whether there is room to do another daily today.</summary>
    public bool HasDailySlot
    {
        get
        {
            for (int slot = 0; slot < MaxDaily; slot++)
            {
                if (owner.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_DAILY_QUESTS_1 + slot) == 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Clears the day's record.</summary>
    public void ResetDaily()
    {
        for (int slot = 0; slot < MaxDaily; slot++)
        {
            owner.Fields.SetUInt32(UpdateFields.PLAYER_FIELD_DAILY_QUESTS_1 + slot, 0);
        }
    }

    /// <summary>Clears the week's record.</summary>
    public void ResetWeekly() => _weekly.Clear();

    /// <summary>Clears the month's record.</summary>
    public void ResetMonthly() => _monthly.Clear();

    /// <summary>Puts a saved set back, without re-running any of the rules.</summary>
    public void Restore(IEnumerable<uint> daily, IEnumerable<uint> weekly, IEnumerable<uint> monthly)
    {
        ArgumentNullException.ThrowIfNull(daily);
        ArgumentNullException.ThrowIfNull(weekly);
        ArgumentNullException.ThrowIfNull(monthly);

        ResetDaily();

        int slot = 0;

        foreach (uint questId in daily)
        {
            if (slot >= MaxDaily)
            {
                break;
            }

            owner.Fields.SetUInt32(UpdateFields.PLAYER_FIELD_DAILY_QUESTS_1 + slot, questId);
            slot++;
        }

        _weekly.Clear();
        _weekly.UnionWith(weekly);

        _monthly.Clear();
        _monthly.UnionWith(monthly);
    }
}

/// <summary>
/// When the shared daily, weekly and monthly resets fall.
/// </summary>
/// <remarks>
/// Port of the reset-time arithmetic in <c>World::InitDailyQuestResetTime</c> and its siblings.
/// <b>These are server-wide instants, not per-character timers.</b> A character who did a daily one
/// minute before the reset may do it again one minute after; counting twenty-four hours from when
/// they did it instead lets a player drift their reset later and later each day.
/// </remarks>
public static class QuestResetTime
{
    /// <summary>The hour of the day the daily reset falls on, server time.</summary>
    public const int DailyResetHour = 3;

    /// <summary>The next daily reset at or after a given moment.</summary>
    public static DateTime NextDaily(DateTime now)
    {
        DateTime today = new(now.Year, now.Month, now.Day, DailyResetHour, 0, 0, now.Kind);

        return now < today ? today : today.AddDays(1);
    }

    /// <summary>
    /// The next weekly reset at or after a given moment.
    /// </summary>
    /// <remarks>
    /// Wednesday, at the daily reset hour — the raid lockout day, which is what weeklies follow.
    /// </remarks>
    public static DateTime NextWeekly(DateTime now)
    {
        DateTime next = NextDaily(now);

        while (next.DayOfWeek != DayOfWeek.Wednesday)
        {
            next = next.AddDays(1);
        }

        return next;
    }

    /// <summary>
    /// The next monthly reset at or after a given moment.
    /// </summary>
    /// <remarks>
    /// The first of the month, at the daily reset hour. Built by adding a month to the first rather
    /// than by adding thirty days, because months are not all the same length and the drift shows
    /// up as a reset that walks backwards through the calendar.
    /// </remarks>
    public static DateTime NextMonthly(DateTime now)
    {
        DateTime first = new(now.Year, now.Month, 1, DailyResetHour, 0, 0, now.Kind);

        return now < first ? first : first.AddMonths(1);
    }
}
