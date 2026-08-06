namespace WowEmu.Protocol;

/// <summary>
/// Which per-index flag table an object uses. One per <c>TYPEID_*</c> group upstream.
/// </summary>
/// <remarks>
/// Items and containers share a table, and so do creatures and players — in both cases the smaller
/// object's fields are a prefix of the larger one's, which is why <c>CONTAINER_END</c> and
/// <c>PLAYER_END</c> size them.
/// </remarks>
public enum UpdateObjectKind
{
    Item,
    Unit,
    GameObject,
    DynamicObject,
    Corpse,
}

/// <summary>
/// Decides which update fields a given observer is allowed to receive.
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>Object::GetUpdateFieldData</c> and the field loop of <c>Unit::BuildValuesUpdate</c>.
/// The same object goes on the wire differently for different people: a player's own client is told
/// things — coinage, bag contents, quest log — that a stranger standing next to them must not learn.
/// </para>
/// <para>
/// Until now nothing computed this, and the only reason it was not a leak is that values blocks were
/// never sent to anyone but the object's owner. That is also why other players never saw each other's
/// health or equipment change: broadcasting the block was unsafe, so it was not done. This is the
/// piece that makes it safe.
/// </para>
/// </remarks>
public static class UpdateFieldVisibilityRules
{
    /// <summary>
    /// Fields upstream sends to everyone regardless of visibility.
    /// </summary>
    /// <remarks>
    /// <c>Object::Object</c> sets <c>_fieldNotifyFlags = UF_FLAG_DYNAMIC</c>, and the field loop
    /// admits an index when <c>_fieldNotifyFlags &amp; flags[index]</c> — so a dynamic field bypasses
    /// the visibility test entirely. Reading only the second half of that condition would drop
    /// <c>UNIT_DYNAMIC_FLAGS</c> for every observer, which is the flag that marks a corpse lootable.
    /// </remarks>
    public const UpdateFieldVisibility AlwaysNotified = UpdateFieldVisibility.Dynamic;

    /// <summary>The per-index flag table for an object kind.</summary>
    public static ReadOnlySpan<ushort> FlagsFor(UpdateObjectKind kind) => kind switch
    {
        UpdateObjectKind.Item => UpdateFieldFlags.Item,
        UpdateObjectKind.Unit => UpdateFieldFlags.Unit,
        UpdateObjectKind.GameObject => UpdateFieldFlags.GameObject,
        UpdateObjectKind.DynamicObject => UpdateFieldFlags.DynamicObject,
        UpdateObjectKind.Corpse => UpdateFieldFlags.Corpse,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// The flags an observer may see on this object.
    /// </summary>
    /// <param name="kind">Which object is being looked at.</param>
    /// <param name="isSelf">The observer <i>is</i> the object.</param>
    /// <param name="isOwner">The observer owns the object — a pet's master, an item's holder.</param>
    /// <remarks>
    /// <para>
    /// Two things upstream adds that we cannot yet: <c>UF_FLAG_SPECIAL_INFO</c>, which needs an
    /// Empathy-style aura cast by the observer, and <c>UF_FLAG_PARTY_MEMBER</c>, which needs groups.
    /// Both are omitted rather than assumed, so the answer is never more permissive than upstream's —
    /// a party member currently sees a stranger's view of their group, which is a missing feature
    /// rather than a leak.
    /// </para>
    /// <para>
    /// Note that <see cref="UpdateFieldVisibility.Owner"/> is <i>not</i> implied by
    /// <paramref name="isSelf"/>. Upstream adds it only when the object's owner guid matches the
    /// observer, and a player does not own itself — its owner guid is zero.
    /// </para>
    /// </remarks>
    public static UpdateFieldVisibility VisibleTo(UpdateObjectKind kind, bool isSelf, bool isOwner)
    {
        UpdateFieldVisibility visible = UpdateFieldVisibility.Public;

        if (isSelf)
        {
            visible |= UpdateFieldVisibility.Private;
        }

        if (isOwner)
        {
            visible |= UpdateFieldVisibility.Owner;

            if (kind == UpdateObjectKind.Item)
            {
                visible |= UpdateFieldVisibility.ItemOwner;
            }
        }

        return visible;
    }

    /// <summary>
    /// Clears every bit in <paramref name="mask"/> the observer is not allowed to see.
    /// </summary>
    /// <remarks>
    /// Indices past the end of the flag table are cleared. A mask longer than the table means the
    /// object claims more fields than its type has, and guessing "probably public" for those is how
    /// a leak gets shipped — refusing to send them is the safe direction to be wrong.
    /// </remarks>
    public static void Filter(UpdateMask mask, UpdateObjectKind kind, UpdateFieldVisibility visible)
    {
        ArgumentNullException.ThrowIfNull(mask);

        ReadOnlySpan<ushort> flags = FlagsFor(kind);

        for (int index = 0; index < mask.FieldCount; index++)
        {
            if (!mask.IsSet(index))
            {
                continue;
            }

            if (index >= flags.Length)
            {
                mask.Unset(index);
                continue;
            }

            UpdateFieldVisibility field = (UpdateFieldVisibility)flags[index];

            if ((field & (visible | AlwaysNotified)) == 0)
            {
                mask.Unset(index);
            }
        }
    }
}
