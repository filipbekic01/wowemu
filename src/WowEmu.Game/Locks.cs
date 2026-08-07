using WowEmu.Data.Client;

namespace WowEmu.Game;

/// <summary>What kind of thing opens a lock. <c>LockType</c>.</summary>
/// <remarks>
/// <b>Not a skill id.</b> A lock's index column holds one of these, and it has to be translated —
/// reading it as a skill directly asks for skill 1, 2 and 3, none of which are the right ones.
/// </remarks>
public static class LockType
{
    public const uint Picklock = 1;
    public const uint Herbalism = 2;
    public const uint Mining = 3;
    public const uint Fishing = 19;
    public const uint Inscription = 20;
}

/// <summary>Why something could not be opened.</summary>
public enum LockResult
{
    /// <summary>It opens.</summary>
    Ok,

    /// <summary>Locked, and this character has neither the key nor the skill.</summary>
    Locked,

    /// <summary>The lock row is missing, so nothing can be said about it.</summary>
    Unknown,
}

/// <summary>
/// Whether a character can open a locked thing.
/// </summary>
/// <remarks>
/// Port of the lock half of <c>Spell::SendLoot</c> and <c>Player::CanOpenLock</c>. It is what stands
/// between the loot table and the player for almost every chest in the game: 38,481 of the 38,594
/// spawned chests carry a lock id.
/// </remarks>
public static class Locks
{
    /// <summary>
    /// The skill a lock type is opened with, or 0.
    /// </summary>
    /// <remarks>
    /// Port of <c>SkillByLockType</c>. Only five of the twenty-odd lock types map to a skill; the
    /// rest are opened by a spell or by hand and have no skill requirement at all.
    /// </remarks>
    public static uint SkillFor(uint lockType) => lockType switch
    {
        LockType.Picklock => SkillType.Lockpicking,
        LockType.Herbalism => Herbalism,
        LockType.Mining => Mining,
        LockType.Fishing => SkillType.Fishing,
        LockType.Inscription => Inscription,
        _ => 0,
    };

    /// <summary>
    /// Whether this character can open something with the given lock.
    /// </summary>
    /// <param name="lockId">Zero for something that is not locked at all.</param>
    /// <remarks>
    /// <b>Any one of the eight cases is enough.</b> A chest may list a key and a lockpicking
    /// requirement, and either opens it — requiring all of them makes almost every locked thing in
    /// the game impossible, and the data looks perfectly reasonable while it does.
    /// <para>
    /// A lock whose every case is empty is not locked. Those rows exist, and treating a row's
    /// presence as "locked" refuses them all.
    /// </para>
    /// </remarks>
    public static LockResult CanOpen(Player player, uint lockId, DbcStore<LockEntry>? locks)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (lockId == 0)
        {
            return LockResult.Ok;
        }

        if (locks is null)
        {
            // No table loaded. Refusing everything would make every chest in the world dead; this
            // is the same behaviour as before locks existed.
            return LockResult.Ok;
        }

        if (!locks.TryGet(lockId, out LockEntry? entry) || entry is null)
        {
            return LockResult.Unknown;
        }

        bool anyRequirement = false;

        for (int i = 0; i < LockEntry.Cases; i++)
        {
            uint type = entry.Types[i];

            if (type == LockEntry.KeyNone)
            {
                continue;
            }

            anyRequirement = true;

            if (type == LockEntry.KeyItem)
            {
                if (entry.Indices[i] != 0 && player.Inventory.CountOf(entry.Indices[i]) > 0)
                {
                    return LockResult.Ok;
                }

                continue;
            }

            if (type != LockEntry.KeySkill)
            {
                continue;
            }

            uint skill = SkillFor(entry.Indices[i]);

            // A lock type with no skill behind it is opened by a spell rather than by anyone
            // standing there, so it is not a way in on its own.
            if (skill == 0)
            {
                continue;
            }

            // The bonused value, which is what the character sheet shows and what a key-shaped
            // trinket or an enchant is bought for.
            if (player.Skills.Value(skill) >= entry.Skills[i])
            {
                return LockResult.Ok;
            }
        }

        return anyRequirement ? LockResult.Locked : LockResult.Ok;
    }

    /// <summary>Herbalism and the two that are not in <see cref="SkillType"/> yet.</summary>
    private const uint Herbalism = 182;
    private const uint Mining = 186;
    private const uint Inscription = 773;
}
