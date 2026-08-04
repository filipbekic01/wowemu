using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The generated update-field table.
/// </summary>
/// <remarks>
/// Indices are cumulative — player fields start where unit fields end, which start where object
/// fields end — so a single wrong base shifts a hundred fields at once. The block boundaries below
/// are the ones PLAN.md §5 lists, and they were written down from the client long before this
/// generator existed, which makes them an independent check on it rather than a restatement.
/// <para>
/// A wrong index never throws. It writes a valid <c>uint32</c> into the wrong slot and the client
/// renders something subtly wrong somewhere else, which is why these are pinned exactly.
/// </para>
/// </remarks>
public sealed class UpdateFieldsTests
{
    [Fact]
    public void BlockBoundaries_MatchTheClientLayout()
    {
        Assert.Equal(6, UpdateFields.OBJECT_END);
        Assert.Equal(64, UpdateFields.ITEM_END);
        Assert.Equal(138, UpdateFields.CONTAINER_END);
        Assert.Equal(148, UpdateFields.UNIT_END);
        Assert.Equal(1326, UpdateFields.PLAYER_END);
        Assert.Equal(18, UpdateFields.GAMEOBJECT_END);
        Assert.Equal(12, UpdateFields.DYNAMICOBJECT_END);
        Assert.Equal(36, UpdateFields.CORPSE_END);
    }

    /// <summary>Every object starts with its guid at index 0; everything else builds on that.</summary>
    [Fact]
    public void ObjectFields_StartAtZero()
    {
        Assert.Equal(0, UpdateFields.OBJECT_FIELD_GUID);
        Assert.Equal(2, UpdateFields.OBJECT_FIELD_TYPE);
        Assert.Equal(3, UpdateFields.OBJECT_FIELD_ENTRY);
        Assert.Equal(4, UpdateFields.OBJECT_FIELD_SCALE_X);
    }

    /// <summary>Unit fields continue where object fields stop, not from zero.</summary>
    [Fact]
    public void UnitFields_AreOffsetByTheObjectBlock()
    {
        Assert.Equal(UpdateFields.OBJECT_END, UpdateFields.UNIT_FIELD_CHARM);
        Assert.True(UpdateFields.UNIT_FIELD_HEALTH > UpdateFields.OBJECT_END);
        Assert.True(UpdateFields.UNIT_FIELD_HEALTH < UpdateFields.UNIT_END);
    }

    [Fact]
    public void PlayerFields_AreOffsetByTheUnitBlock()
    {
        Assert.Equal(UpdateFields.UNIT_END, UpdateFields.PLAYER_DUEL_ARBITER);
        Assert.True(UpdateFields.PLAYER_FLAGS > UpdateFields.UNIT_END);
        Assert.True(UpdateFields.PLAYER_FLAGS < UpdateFields.PLAYER_END);
    }

    /// <summary>
    /// A LONG occupies two consecutive slots. Writing one as a single uint32 leaves the high half
    /// stale — which for a guid field means the client tracks the wrong object.
    /// </summary>
    [Fact]
    public void GuidFields_AreTwoSlotsWide()
    {
        UpdateFieldInfo guid = Find(UpdateFields.OBJECT_FIELD_GUID);

        Assert.Equal(UpdateFieldType.Long, guid.Type);
        Assert.Equal(2, guid.Size);

        // The next named field starts two slots later, not one.
        Assert.Equal(UpdateFields.OBJECT_FIELD_GUID + 2, UpdateFields.OBJECT_FIELD_TYPE);
    }

    [Fact]
    public void ScaleIsAFloat_AndBytesFieldsArePacked()
    {
        Assert.Equal(UpdateFieldType.Float, Find(UpdateFields.OBJECT_FIELD_SCALE_X).Type);

        UpdateFieldInfo playerBytes = Find(UpdateFields.PLAYER_BYTES);
        Assert.Equal(UpdateFieldType.Bytes, playerBytes.Type);
        Assert.Equal(1, playerBytes.Size);
    }

    /// <summary>
    /// Visibility decides what each observer is sent. Marking a private field public leaks a
    /// player's state to everyone standing nearby.
    /// </summary>
    [Fact]
    public void VisibilityFlags_AreCarriedThrough()
    {
        Assert.True(Find(UpdateFields.OBJECT_FIELD_GUID).Flags.HasFlag(UpdateFieldFlag.Public));

        // Coinage is the player's own business.
        Assert.True(Find(UpdateFields.PLAYER_FIELD_COINAGE).Flags.HasFlag(UpdateFieldFlag.Private));
    }

    [Fact]
    public void Table_CoversEveryNamedField()
    {
        Assert.True(UpdateFields.All.Count > 350, $"only {UpdateFields.All.Count} fields have metadata");
        Assert.All(UpdateFields.All, field => Assert.True(field.Size >= 1));
        Assert.All(UpdateFields.All, field => Assert.False(string.IsNullOrEmpty(field.Name)));
    }

    [Fact]
    public void Find_ReturnsNullForAnUnnamedIndex()
    {
        Assert.Null(UpdateFields.Find(99999));
    }

    private static UpdateFieldInfo Find(int index)
    {
        UpdateFieldInfo? info = UpdateFields.Find(index);
        Assert.NotNull(info);
        return info.Value;
    }
}
