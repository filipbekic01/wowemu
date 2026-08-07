using WowEmu.Core;

namespace WowEmu.Game;

/// <summary>
/// Every group on the realm.
/// </summary>
/// <remarks>
/// Port of the group half of <c>GroupMgr</c>. <b>Groups are not owned by a map.</b> A party can span
/// continents, and hanging them off a map would dissolve one the moment two members walked through
/// different portals.
/// <para>
/// Touched only from the world tick, like the session registry, so there is no lock — one would be
/// guarding against a caller that does not exist, and its presence would suggest one did.
/// </para>
/// </remarks>
public sealed class GroupRegistry
{
    private readonly Dictionary<ulong, Group> _groups = [];

    /// <summary>Which group each character is in, if any.</summary>
    private readonly Dictionary<ObjectGuid, Group> _byMember = [];

    /// <summary>Who has an outstanding invitation, and to which group.</summary>
    private readonly Dictionary<ObjectGuid, Group> _byInvite = [];

    private uint _nextCounter = 1;

    /// <summary>How many groups exist.</summary>
    public int Count => _groups.Count;

    /// <summary>Every group, in no particular order.</summary>
    public IReadOnlyCollection<Group> All => _groups.Values;

    /// <summary>The group a character is in, or null.</summary>
    public Group? GroupOf(ObjectGuid member) =>
        _byMember.TryGetValue(member, out Group? group) ? group : null;

    /// <summary>The group a character has been invited to, or null.</summary>
    public Group? InviteFor(ObjectGuid invitee) =>
        _byInvite.TryGetValue(invitee, out Group? group) ? group : null;

    /// <summary>
    /// Starts a group with one member.
    /// </summary>
    /// <remarks>
    /// <b>A group exists before its second member accepts.</b> Upstream creates it at the moment of
    /// the invite so the inviter's own membership is settled — without it, two simultaneous invites
    /// from the same player would each start their own group.
    /// </remarks>
    public Group Create(ObjectGuid leader, string leaderName)
    {
        Group group = new() { Guid = ObjectGuid.Create(HighGuid.Group, _nextCounter++) };

        group.Add(leader, leaderName);
        _groups[group.Guid.Value] = group;
        _byMember[leader] = group;

        return group;
    }

    /// <summary>Records an invitation, so the invitee's accept can find the group.</summary>
    public void Invite(Group group, ObjectGuid invitee)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.Invite(invitee);
        _byInvite[invitee] = group;
    }

    /// <summary>Forgets an invitation.</summary>
    public void Uninvite(Group group, ObjectGuid invitee)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.Uninvite(invitee);
        _byInvite.Remove(invitee);
    }

    /// <summary>
    /// Adds a character to a group.
    /// </summary>
    /// <returns>False when the group is full.</returns>
    public bool Add(Group group, ObjectGuid member, string name)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (!group.Add(member, name))
        {
            return false;
        }

        _byMember[member] = group;
        _byInvite.Remove(member);

        return true;
    }

    /// <summary>
    /// Takes a character out of their group, disbanding it if too few are left.
    /// </summary>
    /// <returns>Whether the group was disbanded.</returns>
    /// <remarks>
    /// <b>A party of one is not a party.</b> Upstream disbands below two members, so the last
    /// person left is not stranded in a group the client still draws a frame for. A raid is held to
    /// the same floor — it is the same object.
    /// </remarks>
    public bool Remove(Group group, ObjectGuid member)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (!group.Remove(member))
        {
            return false;
        }

        _byMember.Remove(member);

        if (group.Members.Count >= 2)
        {
            return false;
        }

        Disband(group);

        return true;
    }

    /// <summary>Dissolves a group and forgets everyone in it.</summary>
    public void Disband(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);

        foreach (GroupMember member in group.Members)
        {
            _byMember.Remove(member.Guid);
        }

        foreach (ObjectGuid invitee in group.Invited)
        {
            _byInvite.Remove(invitee);
        }

        _groups.Remove(group.Guid.Value);
    }

    /// <summary>Puts a saved group back, without re-running any of the rules.</summary>
    public Group Restore(
        ObjectGuid groupGuid, ObjectGuid leader, byte type, IReadOnlyList<GroupMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        Group group = new() { Guid = groupGuid };

        group.Restore(leader, type, members);

        _groups[groupGuid.Value] = group;

        foreach (GroupMember member in members)
        {
            _byMember[member.Guid] = group;
        }

        // Guids are handed out from a counter, so a restored group has to push it past anything
        // already taken — otherwise the next new group collides with one that already exists.
        if (groupGuid.Counter >= _nextCounter)
        {
            _nextCounter = groupGuid.Counter + 1;
        }

        return group;
    }
}
