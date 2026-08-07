using WowEmu.Core;

namespace WowEmu.Game;

/// <summary>What kind of group this is. <c>GroupType</c>, a flag set.</summary>
public static class GroupType
{
    public const byte Normal = 0x00;
    public const byte Battleground = 0x01;
    public const byte Raid = 0x02;
    public const byte LfgRestricted = 0x04;
    public const byte Lfg = 0x08;
}

/// <summary>What a member is, beyond being in the group. <c>GroupMemberFlags</c>.</summary>
public static class GroupMemberFlags
{
    public const byte None = 0x00;
    public const byte Assistant = 0x01;
    public const byte MainTank = 0x02;
    public const byte MainAssist = 0x04;
}

/// <summary>How a member appears in the party frame. <c>GroupMemberOnlineStatus</c>.</summary>
public static class GroupMemberStatus
{
    public const byte Offline = 0x00;
    public const byte Online = 0x01;
    public const byte Pvp = 0x02;
    public const byte Dead = 0x04;
    public const byte Ghost = 0x08;
}

/// <summary>How a group divides its loot. <c>LootMethod</c>.</summary>
public static class LootMethod
{
    public const byte FreeForAll = 0;
    public const byte RoundRobin = 1;
    public const byte MasterLoot = 2;
    public const byte GroupLoot = 3;
    public const byte NeedBeforeGreed = 4;
}

/// <summary>Why a party operation was refused. <c>PartyResult</c>.</summary>
public enum PartyResult : uint
{
    Ok = 0,
    BadPlayerName = 1,
    TargetNotInGroup = 2,
    TargetNotInInstance = 3,
    GroupFull = 4,
    AlreadyInGroup = 5,
    NotInGroup = 6,
    NotLeader = 7,
    WrongFaction = 8,
    IgnoringYou = 9,
    InviteRestricted = 13,
    RaidDisallowedByLevel = 25,
}

/// <summary>Which operation a <c>SMSG_PARTY_COMMAND_RESULT</c> is answering. <c>PartyOperation</c>.</summary>
public static class PartyOperation
{
    public const uint Invite = 0;
    public const uint Uninvite = 1;
    public const uint Leave = 2;
    public const uint Swap = 4;
}

/// <summary>One member's place in a group.</summary>
/// <param name="SubGroup">
/// Which of the eight raid sub-groups. Always zero in a party — the field exists for both, and the
/// client draws a raid frame from it.
/// </param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Guid is the client's own vocabulary for these; renaming would obscure the port.")]
public sealed record GroupMember(
    ObjectGuid Guid,
    string Name,
    byte SubGroup = 0,
    byte Flags = GroupMemberFlags.None,
    byte Roles = 0);

/// <summary>
/// A party or raid.
/// </summary>
/// <remarks>
/// Port of <c>Group</c>. <b>A party and a raid are the same object with a flag</b> — converting one
/// to the other raises the size limit and turns on sub-groups, and nothing else changes. Modelling
/// them separately means every caller has to know which it is holding.
/// <para>
/// The group is authoritative over its members; a <see cref="Player"/> holds only a reference back.
/// Two records of who is in a group are two records that can disagree, and the disagreement shows
/// up as a player in a group that does not have them.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Guid is the client's own vocabulary for these; renaming would obscure the port.")]
public sealed class Group
{
    /// <summary>How many fit in a party. <c>MAXGROUPSIZE</c>.</summary>
    public const int PartySize = 5;

    /// <summary>How many fit in a raid. <c>MAXRAIDSIZE</c>.</summary>
    public const int RaidSize = 40;

    /// <summary>How many sub-groups a raid has. <c>MAX_RAID_SUBGROUPS</c>.</summary>
    public const int SubGroups = RaidSize / PartySize;

    private readonly List<GroupMember> _members = [];
    private readonly HashSet<ObjectGuid> _invited = [];

    /// <summary>The group's own guid, which the client keys its frames on.</summary>
    public ObjectGuid Guid { get; init; }

    /// <summary>Everyone in the group, in join order. The leader is not necessarily first.</summary>
    public IReadOnlyList<GroupMember> Members => _members;

    /// <summary>Who has been invited but has not answered.</summary>
    public IReadOnlyCollection<ObjectGuid> Invited => _invited;

    /// <summary>Whose group it is.</summary>
    public ObjectGuid Leader { get; private set; }

    /// <summary>The group type flags. <see cref="GroupType"/>.</summary>
    public byte Type { get; private set; } = GroupType.Normal;

    /// <summary>Whether this is a raid rather than a party.</summary>
    public bool IsRaid => (Type & GroupType.Raid) != 0;

    /// <summary>How many members fit, which the raid flag decides.</summary>
    public int Capacity => IsRaid ? RaidSize : PartySize;

    /// <summary>Whether nobody else fits.</summary>
    public bool IsFull => _members.Count >= Capacity;

    /// <summary>How the group divides its loot.</summary>
    public byte LootMethod { get; set; } = Game.LootMethod.GroupLoot;

    /// <summary>Who decides, under master loot.</summary>
    public ObjectGuid MasterLooter { get; set; }

    /// <summary>The quality at or above which a roll is held. Uncommon by default.</summary>
    public byte LootThreshold { get; set; } = 2;

    /// <summary>
    /// Whose turn it is under round-robin.
    /// </summary>
    /// <remarks>
    /// Kept on the group rather than derived from the kill: round-robin is a rotation across the
    /// whole session, and recomputing it per corpse gives the first member every drop.
    /// </remarks>
    public ObjectGuid Looter { get; private set; }

    /// <summary>
    /// A counter the client wants incremented on every group list it receives.
    /// </summary>
    /// <remarks>
    /// 3.3 only. The client uses it to discard a list that arrives out of order — sending a
    /// constant makes it drop every update after the first.
    /// </remarks>
    public uint UpdateCounter { get; private set; }

    /// <summary>Bumps and returns the list counter.</summary>
    public uint NextCounter() => ++UpdateCounter;

    /// <summary>Whether a character is in this group.</summary>
    public bool Contains(ObjectGuid guid) => Find(guid) is not null;

    /// <summary>A member's slot, or null.</summary>
    public GroupMember? Find(ObjectGuid guid)
    {
        foreach (GroupMember member in _members)
        {
            if (member.Guid == guid)
            {
                return member;
            }
        }

        return null;
    }

    /// <summary>Whether a character leads this group.</summary>
    public bool IsLeader(ObjectGuid guid) => Leader == guid;

    /// <summary>
    /// Whether a character may invite, kick and set the loot rules.
    /// </summary>
    /// <remarks>
    /// <b>Assistants can do most of what a leader can.</b> Checking only for the leader makes
    /// assistants decorative, which is the whole of what a raid assistant is for.
    /// </remarks>
    public bool CanCommand(ObjectGuid guid) =>
        IsLeader(guid) || (Find(guid)?.Flags & GroupMemberFlags.Assistant) != 0;

    /// <summary>Records an outstanding invitation.</summary>
    public void Invite(ObjectGuid guid) => _invited.Add(guid);

    /// <summary>Forgets an invitation, answered or not.</summary>
    public bool Uninvite(ObjectGuid guid) => _invited.Remove(guid);

    /// <summary>Whether this character has been invited and not yet answered.</summary>
    public bool IsInvited(ObjectGuid guid) => _invited.Contains(guid);

    /// <summary>
    /// Adds a member.
    /// </summary>
    /// <returns>False when the group is full or they are already in it.</returns>
    /// <remarks>
    /// The first member added becomes the leader. A group with no leader cannot be commanded at
    /// all, and the client draws a crown on somebody regardless.
    /// </remarks>
    public bool Add(ObjectGuid guid, string name, byte subGroup = 0)
    {
        if (IsFull || Contains(guid))
        {
            return false;
        }

        _invited.Remove(guid);
        _members.Add(new GroupMember(guid, name, IsRaid ? subGroup : (byte)0));

        if (Leader.IsEmpty)
        {
            Leader = guid;
        }

        if (Looter.IsEmpty)
        {
            Looter = guid;
        }

        return true;
    }

    /// <summary>
    /// Removes a member.
    /// </summary>
    /// <returns>False when they were not in the group.</returns>
    /// <remarks>
    /// <b>Leadership and the round-robin turn both move when their holder leaves.</b> Leaving
    /// either pointing at somebody who is gone makes the group uncommandable and stalls the loot
    /// rotation, neither of which is visible until somebody tries.
    /// </remarks>
    public bool Remove(ObjectGuid guid)
    {
        int index = _members.FindIndex(member => member.Guid == guid);

        if (index < 0)
        {
            return false;
        }

        _members.RemoveAt(index);

        if (Leader == guid)
        {
            Leader = _members.Count > 0 ? _members[0].Guid : ObjectGuid.Empty;
        }

        if (Looter == guid)
        {
            Looter = _members.Count > 0 ? _members[0].Guid : ObjectGuid.Empty;
        }

        return true;
    }

    /// <summary>
    /// Hands leadership to another member.
    /// </summary>
    /// <returns>False when they are not in the group.</returns>
    public bool SetLeader(ObjectGuid guid)
    {
        if (!Contains(guid))
        {
            return false;
        }

        Leader = guid;

        return true;
    }

    /// <summary>Sets or clears a member's assistant, main-tank or main-assist flag.</summary>
    /// <returns>False when they are not in the group.</returns>
    public bool SetFlag(ObjectGuid guid, byte flag, bool on)
    {
        int index = _members.FindIndex(member => member.Guid == guid);

        if (index < 0)
        {
            return false;
        }

        byte flags = _members[index].Flags;

        _members[index] = _members[index] with
        {
            Flags = (byte)(on ? flags | flag : flags & ~flag),
        };

        return true;
    }

    /// <summary>
    /// Turns a party into a raid.
    /// </summary>
    /// <remarks>
    /// <b>One-way.</b> There is no convert-back in 3.3.5, and offering one would leave a raid of
    /// twenty people trying to become a party of five.
    /// </remarks>
    public void ConvertToRaid() => Type |= GroupType.Raid;

    /// <summary>
    /// Moves a member to another sub-group.
    /// </summary>
    /// <returns>False outside a raid, or when the target sub-group is full.</returns>
    /// <remarks>
    /// Sub-groups hold five each. Letting one overfill puts members off the bottom of the raid
    /// frame, where the client simply does not draw them.
    /// </remarks>
    public bool SetSubGroup(ObjectGuid guid, byte subGroup)
    {
        if (!IsRaid || subGroup >= SubGroups)
        {
            return false;
        }

        int index = _members.FindIndex(member => member.Guid == guid);

        if (index < 0 || _members[index].SubGroup == subGroup)
        {
            return false;
        }

        int occupants = 0;

        foreach (GroupMember member in _members)
        {
            if (member.SubGroup == subGroup)
            {
                occupants++;
            }
        }

        if (occupants >= PartySize)
        {
            return false;
        }

        _members[index] = _members[index] with { SubGroup = subGroup };

        return true;
    }

    /// <summary>
    /// Advances the round-robin turn to the next member.
    /// </summary>
    /// <returns>Whose turn it now is.</returns>
    /// <remarks>
    /// <b>Advanced per corpse, not per item.</b> Round-robin gives one player a whole corpse and
    /// then moves on; advancing per item interleaves the members across a single body, which is
    /// what group loot does and not what round-robin means.
    /// </remarks>
    public ObjectGuid AdvanceLooter()
    {
        if (_members.Count == 0)
        {
            return Looter = ObjectGuid.Empty;
        }

        int index = _members.FindIndex(member => member.Guid == Looter);

        return Looter = _members[(index + 1) % _members.Count].Guid;
    }

    /// <summary>Puts a saved group back, without re-running any of the rules.</summary>
    public void Restore(ObjectGuid leader, byte type, IEnumerable<GroupMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        Type = type;
        _members.Clear();
        _members.AddRange(members);
        Leader = leader;

        if (Looter.IsEmpty && _members.Count > 0)
        {
            Looter = _members[0].Guid;
        }
    }
}
