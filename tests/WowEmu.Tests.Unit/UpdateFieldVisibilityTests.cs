using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Which update fields each observer is allowed to receive.
/// </summary>
/// <remarks>
/// The flag tables are generated from AzerothCore's <c>UpdateFieldFlags.cpp</c>, so what is worth
/// pinning here is not their contents but the rules applied on top: that the tables are the right
/// length, that a stranger cannot see private fields, that an owner can, and that dynamic fields
/// reach everybody regardless.
/// </remarks>
public sealed class UpdateFieldVisibilityTests
{
    /// <summary>
    /// Each table must cover its whole object.
    /// </summary>
    /// <remarks>
    /// The length is the check that catches a regenerated table drifting out of step with the field
    /// indices. A table one entry short would silently strip the last field of every object, and a
    /// field that stops arriving looks like a gameplay bug, not a codegen one.
    /// </remarks>
    [Theory]
    [InlineData(UpdateObjectKind.Item, UpdateFields.CONTAINER_END)]
    [InlineData(UpdateObjectKind.Unit, UpdateFields.PLAYER_END)]
    [InlineData(UpdateObjectKind.GameObject, UpdateFields.GAMEOBJECT_END)]
    [InlineData(UpdateObjectKind.DynamicObject, UpdateFields.DYNAMICOBJECT_END)]
    [InlineData(UpdateObjectKind.Corpse, UpdateFields.CORPSE_END)]
    public void EachFlagTable_CoversEveryFieldOfItsType(UpdateObjectKind kind, int expected) =>
        Assert.Equal(expected, UpdateFieldVisibilityRules.FlagsFor(kind).Length);

    [Fact]
    public void AStranger_SeesOnlyPublicFields()
    {
        UpdateFieldVisibility visible =
            UpdateFieldVisibilityRules.VisibleTo(UpdateObjectKind.Unit, isSelf: false, isOwner: false);

        Assert.Equal(UpdateFieldVisibility.Public, visible);
    }

    [Fact]
    public void TheObjectItself_AlsoSeesPrivateFields()
    {
        UpdateFieldVisibility visible =
            UpdateFieldVisibilityRules.VisibleTo(UpdateObjectKind.Unit, isSelf: true, isOwner: false);

        Assert.True(visible.HasFlag(UpdateFieldVisibility.Private));
    }

    /// <summary>
    /// Being the object does not make you its owner.
    /// </summary>
    /// <remarks>
    /// Upstream adds <c>UF_FLAG_OWNER</c> only when the object's owner guid equals the observer's,
    /// and a player's owner guid is zero — owner means a pet's master or an item's holder. Conflating
    /// the two would hand a player the owner-only fields of every unit it happened to be.
    /// </remarks>
    [Fact]
    public void BeingTheObject_DoesNotImplyOwningIt()
    {
        UpdateFieldVisibility visible =
            UpdateFieldVisibilityRules.VisibleTo(UpdateObjectKind.Unit, isSelf: true, isOwner: false);

        Assert.False(visible.HasFlag(UpdateFieldVisibility.Owner));
    }

    /// <summary>An item's holder gets the item-owner flag as well as the plain owner one.</summary>
    [Fact]
    public void AnItemsOwner_GetsBothOwnerFlags()
    {
        UpdateFieldVisibility visible =
            UpdateFieldVisibilityRules.VisibleTo(UpdateObjectKind.Item, isSelf: false, isOwner: true);

        Assert.True(visible.HasFlag(UpdateFieldVisibility.Owner));
        Assert.True(visible.HasFlag(UpdateFieldVisibility.ItemOwner));
    }

    /// <summary>The whole point: a stranger's block loses the private fields.</summary>
    [Fact]
    public void Filtering_ForAStranger_ClearsPrivateFields()
    {
        int privateIndex = FirstIndexWith(UpdateFieldVisibility.Private);
        int publicIndex = FirstIndexWith(UpdateFieldVisibility.Public);

        UpdateMask mask = new(UpdateFields.PLAYER_END);
        mask.Set(privateIndex);
        mask.Set(publicIndex);

        UpdateFieldVisibilityRules.Filter(mask, UpdateObjectKind.Unit, UpdateFieldVisibility.Public);

        Assert.False(mask.IsSet(privateIndex));
        Assert.True(mask.IsSet(publicIndex));
    }

    [Fact]
    public void Filtering_ForTheObjectItself_KeepsPrivateFields()
    {
        int privateIndex = FirstIndexWith(UpdateFieldVisibility.Private);

        UpdateMask mask = new(UpdateFields.PLAYER_END);
        mask.Set(privateIndex);

        UpdateFieldVisibilityRules.Filter(
            mask,
            UpdateObjectKind.Unit,
            UpdateFieldVisibilityRules.VisibleTo(UpdateObjectKind.Unit, isSelf: true, isOwner: false));

        Assert.True(mask.IsSet(privateIndex));
    }

    /// <summary>
    /// Dynamic fields go to everyone, including a stranger.
    /// </summary>
    /// <remarks>
    /// <c>UNIT_DYNAMIC_FLAGS</c> carries only <c>UF_FLAG_DYNAMIC</c>, which is never in any
    /// observer's visible set — it arrives through <c>_fieldNotifyFlags</c> instead. Filtering on
    /// visibility alone would drop it for every observer, taking the lootable marker on a corpse
    /// with it.
    /// </remarks>
    [Fact]
    public void DynamicFields_SurviveFilteringForAStranger()
    {
        int dynamicIndex = FirstIndexWith(UpdateFieldVisibility.Dynamic);

        UpdateMask mask = new(UpdateFields.PLAYER_END);
        mask.Set(dynamicIndex);

        UpdateFieldVisibilityRules.Filter(mask, UpdateObjectKind.Unit, UpdateFieldVisibility.Public);

        Assert.True(mask.IsSet(dynamicIndex));
    }

    /// <summary>A mask longer than the table loses the surplus rather than guessing.</summary>
    [Fact]
    public void IndicesPastTheTable_AreCleared()
    {
        UpdateMask mask = new(UpdateFields.PLAYER_END);
        int beyond = UpdateFields.GAMEOBJECT_END + 1;

        mask.Set(beyond);
        UpdateFieldVisibilityRules.Filter(mask, UpdateObjectKind.GameObject, UpdateFieldVisibility.Public);

        Assert.False(mask.IsSet(beyond));
    }

    /// <summary>
    /// The unit table must actually contain private fields.
    /// </summary>
    /// <remarks>
    /// Guards the guard. If a parsing slip made every entry public, every test above that asserts
    /// "private was removed" would pass vacuously — there would be nothing private to remove.
    /// </remarks>
    [Fact]
    public void TheUnitTable_ContainsBothPublicAndPrivateFields()
    {
        int publics = 0;
        int privates = 0;

        foreach (ushort entry in UpdateFieldFlags.Unit)
        {
            if (((UpdateFieldVisibility)entry).HasFlag(UpdateFieldVisibility.Public))
            {
                publics++;
            }

            if (((UpdateFieldVisibility)entry).HasFlag(UpdateFieldVisibility.Private))
            {
                privates++;
            }
        }

        Assert.True(publics > 100, $"expected many public unit fields, found {publics}");
        Assert.True(privates > 100, $"expected many private player fields, found {privates}");
    }

    private static int FirstIndexWith(UpdateFieldVisibility flag)
    {
        ReadOnlySpan<ushort> flags = UpdateFieldFlags.Unit;

        for (int index = 0; index < flags.Length; index++)
        {
            if (((UpdateFieldVisibility)flags[index]).HasFlag(flag))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"no unit field carries {flag}");
    }
}
