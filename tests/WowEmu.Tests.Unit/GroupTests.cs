using WowEmu.Core;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Parties and raids: membership, command rights, and the round-robin turn.
/// </summary>
public sealed class GroupTests
{
    private static readonly ObjectGuid Alice = ObjectGuid.Create(HighGuid.Player, 1);
    private static readonly ObjectGuid Bob = ObjectGuid.Create(HighGuid.Player, 2);
    private static readonly ObjectGuid Carol = ObjectGuid.Create(HighGuid.Player, 3);

    /// <summary>The first member added leads.</summary>
    /// <remarks>
    /// A group with no leader cannot be commanded at all, and the client draws a crown on somebody
    /// regardless — so an unset leader shows one member wearing it and none of them able to invite.
    /// </remarks>
    [Fact]
    public void TheFirstMember_Leads()
    {
        Group group = Party();

        Assert.True(group.IsLeader(Alice));
        Assert.False(group.IsLeader(Bob));
    }

    /// <summary>
    /// A party holds five; a raid holds forty.
    /// </summary>
    /// <remarks>
    /// The same object with a flag. Modelling them separately means every caller has to know which
    /// it is holding.
    /// </remarks>
    [Fact]
    public void TheRaidFlag_RaisesTheLimit()
    {
        Group group = Party();

        Assert.Equal(Group.PartySize, group.Capacity);

        group.ConvertToRaid();

        Assert.Equal(Group.RaidSize, group.Capacity);
        Assert.True(group.IsRaid);
    }

    /// <summary>A full party refuses another member.</summary>
    [Fact]
    public void AFullParty_RefusesAnother()
    {
        Group group = new();

        for (uint i = 1; i <= Group.PartySize; i++)
        {
            Assert.True(group.Add(ObjectGuid.Create(HighGuid.Player, i), $"P{i}"));
        }

        Assert.True(group.IsFull);
        Assert.False(group.Add(ObjectGuid.Create(HighGuid.Player, 99), "Extra"));
    }

    /// <summary>
    /// Assistants can command; ordinary members cannot.
    /// </summary>
    /// <remarks>
    /// Checking only for the leader makes assistants decorative, which is the whole of what a raid
    /// assistant is for.
    /// </remarks>
    [Fact]
    public void AnAssistant_CanCommand()
    {
        Group group = Party();

        Assert.False(group.CanCommand(Bob));

        group.SetFlag(Bob, GroupMemberFlags.Assistant, on: true);

        Assert.True(group.CanCommand(Bob));

        group.SetFlag(Bob, GroupMemberFlags.Assistant, on: false);

        Assert.False(group.CanCommand(Bob));
    }

    /// <summary>
    /// Leadership moves when the leader leaves.
    /// </summary>
    /// <remarks>
    /// Leaving it pointing at somebody who is gone makes the group uncommandable, which is not
    /// visible until somebody tries to invite.
    /// </remarks>
    [Fact]
    public void LeadershipMoves_WhenTheLeaderLeaves()
    {
        Group group = Party();

        group.Remove(Alice);

        Assert.Equal(Bob, group.Leader);
    }

    /// <summary>And so does the round-robin turn.</summary>
    /// <remarks>
    /// A turn pointing at somebody who left stalls the rotation, and nobody can loot.
    /// </remarks>
    [Fact]
    public void TheLooterTurn_MovesToo()
    {
        Group group = Party();

        Assert.Equal(Alice, group.Looter);

        group.Remove(Alice);

        Assert.Equal(Bob, group.Looter);
    }

    /// <summary>The round-robin turn wraps.</summary>
    [Fact]
    public void TheLooterTurn_Wraps()
    {
        Group group = Party();

        group.Add(Carol, "Carol");

        Assert.Equal(Bob, group.AdvanceLooter());
        Assert.Equal(Carol, group.AdvanceLooter());
        Assert.Equal(Alice, group.AdvanceLooter());
    }

    /// <summary>
    /// A raid sub-group holds five.
    /// </summary>
    /// <remarks>
    /// Letting one overfill puts members off the bottom of the raid frame, where the client simply
    /// does not draw them.
    /// </remarks>
    [Fact]
    public void ASubGroup_HoldsFive()
    {
        Group group = new();

        group.ConvertToRaid();

        // Five into sub-group 1, and one spare sitting in sub-group 0.
        for (uint i = 1; i <= 5; i++)
        {
            group.Add(ObjectGuid.Create(HighGuid.Player, i), $"P{i}", subGroup: 1);
        }

        ObjectGuid spare = ObjectGuid.Create(HighGuid.Player, 6);

        group.Add(spare, "P6", subGroup: 0);

        Assert.False(group.SetSubGroup(spare, 1));
        Assert.Equal(0, group.Find(spare)!.SubGroup);

        // And into a sub-group with room, it moves.
        Assert.True(group.SetSubGroup(spare, 2));
        Assert.Equal(2, group.Find(spare)!.SubGroup);
    }

    /// <summary>Sub-groups do nothing in a party.</summary>
    /// <remarks>
    /// The field exists for both, and honouring it in a party would split a five-person group
    /// across a raid frame it never asked for.
    /// </remarks>
    [Fact]
    public void SubGroups_DoNothingInAParty()
    {
        Group group = Party();

        Assert.False(group.SetSubGroup(Bob, 1));
        Assert.Equal(0, group.Find(Bob)!.SubGroup);
    }

    /// <summary>
    /// The list counter increments on every send.
    /// </summary>
    /// <remarks>
    /// The client discards a list whose counter it has already seen, so a constant makes every
    /// update after the first vanish — the frame simply stops changing.
    /// </remarks>
    [Fact]
    public void TheListCounter_Increments()
    {
        Group group = Party();

        Assert.Equal(1u, group.NextCounter());
        Assert.Equal(2u, group.NextCounter());
    }

    // ------------------------------------------------------------------ the registry

    /// <summary>
    /// A group of one is disbanded.
    /// </summary>
    /// <remarks>
    /// A party of one is not a party, and the client draws a frame for it regardless — leaving the
    /// last person in one strands them in a group they cannot leave.
    /// </remarks>
    [Fact]
    public void AGroupOfOne_IsDisbanded()
    {
        GroupRegistry registry = new();
        Group group = registry.Create(Alice, "Alice");

        registry.Add(group, Bob, "Bob");

        // True means "the group was disbanded", not "the member was removed".
        Assert.True(registry.Remove(group, Bob));
        Assert.Null(registry.GroupOf(Alice));
        Assert.Null(registry.GroupOf(Bob));
        Assert.Equal(0, registry.Count);
    }

    /// <summary>A member lookup finds the group.</summary>
    [Fact]
    public void AMemberLookup_FindsTheGroup()
    {
        GroupRegistry registry = new();
        Group group = registry.Create(Alice, "Alice");

        registry.Add(group, Bob, "Bob");

        Assert.Same(group, registry.GroupOf(Bob));
    }

    /// <summary>
    /// A restored group pushes the guid counter past itself.
    /// </summary>
    /// <remarks>
    /// Guids come from a counter, so a restored group has to move it — otherwise the next new group
    /// collides with one that already exists, and two groups share a frame.
    /// </remarks>
    [Fact]
    public void ARestoredGroup_PushesTheCounter()
    {
        GroupRegistry registry = new();

        ObjectGuid restored = ObjectGuid.Create(HighGuid.Group, 50);

        registry.Restore(restored, Alice, GroupType.Normal, [new GroupMember(Alice, "Alice")]);

        Group fresh = registry.Create(Bob, "Bob");

        Assert.NotEqual(restored, fresh.Guid);
        Assert.True(fresh.Guid.Counter > restored.Counter);
    }

    private static Group Party()
    {
        Group group = new();

        group.Add(Alice, "Alice");
        group.Add(Bob, "Bob");

        return group;
    }
}
