using System.Diagnostics.CodeAnalysis;
using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// A gameobject standing in the world: a door, a chest, a campfire, a signpost, a mailbox.
/// </summary>
/// <remarks>
/// Port of the parts of <c>GameObject</c> that Phase 6 needs. It is a <see cref="WorldObject"/> but
/// <b>not</b> a <see cref="Unit"/>: no level, no health, no movement block. Its field block ends at
/// <c>GAMEOBJECT_END</c>, which is 18 slots against a unit's 148 — sending it as a unit would put
/// 130 fields of nonsense on the wire.
/// <para>
/// The only unusual thing about it is orientation. A creature faces a direction; a gameobject has a
/// full quaternion, because a door can be tilted and a banner can lean. That rotation is packed into
/// 64 bits and sent outside the field block — see <see cref="UpdateBlockBuilder.PackRotation"/>.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "GameObject is the client's own name for this type; renaming it would obscure the port.")]
public sealed class GameObject : WorldObject
{
    private GameObject(ObjectGuid guid)
        : base(guid, TypeId.GameObject, UpdateFields.GAMEOBJECT_END, TypeMask.GameObjectObject)
    {
    }

    /// <summary>The <c>gameobject</c> row this came from — upstream's <c>m_spawnId</c>.</summary>
    public uint SpawnId { get; private init; }

    /// <summary>The <c>gameobject_template</c> entry.</summary>
    public uint Entry { get; private init; }

    /// <summary>
    /// The row it was built from, kept so its type and data columns stay reachable.
    /// </summary>
    /// <remarks>
    /// Held rather than looked up again: the <c>data</c> columns are a union read differently per
    /// type, and every caller that wants one wants the type in the same breath.
    /// </remarks>
    public GameObjectTemplate Template { get; private init; } = null!;

    /// <summary>Which phases can see it. Everything is phase 1 until phasing exists.</summary>
    public uint PhaseMask { get; private init; }

    /// <summary>
    /// The world rotation, packed into the 64 bits the create block carries.
    /// </summary>
    /// <remarks>
    /// Computed once at spawn. Nothing rotates a gameobject yet, and when something does it will
    /// have to recompute this and resend a create block — the rotation is not an update field, so a
    /// values update cannot carry it.
    /// </remarks>
    public ulong PackedRotation { get; private init; }

    /// <summary>
    /// What is inside, once somebody has opened it.
    /// </summary>
    /// <remarks>
    /// Filled the first time a chest is opened rather than at spawn: rolling a loot table for every
    /// chest on the continent up front is 38,594 rolls nobody has looked at.
    /// <para>
    /// An emptied chest keeps its (empty) loot rather than clearing it, so a second player is told
    /// it is empty rather than handed a fresh roll.
    /// </para>
    /// </remarks>
    public Loot? Loot { get; set; }

    /// <summary>What kind of object it is: door, chest, trap, and so on.</summary>
    public byte GoType => Fields.GetByte(UpdateFields.GAMEOBJECT_BYTES_1, 1);

    /// <summary>Ready, active, or destroyed. A closed door and an open one differ only here.</summary>
    public byte GoState => Fields.GetByte(UpdateFields.GAMEOBJECT_BYTES_1, 0);

    public uint DisplayId => Fields.GetUInt32(UpdateFields.GAMEOBJECT_DISPLAYID);

    /// <summary>
    /// Builds a gameobject from its spawn row and template.
    /// </summary>
    /// <remarks>
    /// Port of <c>GameObject::Create</c>. Everything the client needs to draw the object and let it
    /// be clicked lives in seven fields; the rest of <c>GameObject::Create</c> is loot, instance
    /// state and the per-type data block, none of which exists yet.
    /// </remarks>
    public static GameObject Create(GameObjectSpawn spawn, GameObjectTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        GameObject gameObject = new(ObjectGuid.Create(HighGuid.GameObject, spawn.Entry, spawn.SpawnId))
        {
            SpawnId = spawn.SpawnId,
            Entry = spawn.Entry,
            Template = template,
            PhaseMask = spawn.PhaseMask,
            MapId = spawn.MapId,
            Position = spawn.Position,
            PackedRotation = UpdateBlockBuilder.PackRotation(
                spawn.Rotation0,
                spawn.Rotation1,
                spawn.Rotation2,
                spawn.Rotation3,
                spawn.Position.Orientation),
        };

        gameObject.Name = template.Name;

        UpdateFieldStorage fields = gameObject.Fields;

        fields.SetUInt32(UpdateFields.OBJECT_FIELD_ENTRY, spawn.Entry);

        // Upstream sets the scale straight from the template with no floor, and three templates
        // carry a size of zero. Eleven spawns use them, and the client draws them at nothing.
        // Reproduced rather than corrected: they are almost certainly meant to be invisible, and a
        // silent 1.0 here would make eleven objects appear that upstream does not show.
        fields.SetFloat(UpdateFields.OBJECT_FIELD_SCALE_X, template.Size);

        fields.SetUInt32(UpdateFields.GAMEOBJECT_DISPLAYID, template.DisplayId);
        fields.SetUInt32(UpdateFields.GAMEOBJECT_FACTION, template.Faction);
        fields.SetUInt32(UpdateFields.GAMEOBJECT_FLAGS, template.Flags);

        // GAMEOBJECT_BYTES_1 packs four values into one slot: state, type, art kit, animation.
        fields.SetByte(UpdateFields.GAMEOBJECT_BYTES_1, 0, spawn.State);
        fields.SetByte(UpdateFields.GAMEOBJECT_BYTES_1, 1, template.Type);
        fields.SetByte(UpdateFields.GAMEOBJECT_BYTES_1, 2, 0);
        fields.SetByte(UpdateFields.GAMEOBJECT_BYTES_1, 3, spawn.AnimProgress);

        // The parent rotation is the transport-relative one, and is left at identity: nothing here
        // rides a transport. It is a separate thing from the world rotation above.
        fields.SetFloat(UpdateFields.GAMEOBJECT_PARENTROTATION + 3, 1.0f);

        return gameObject;
    }
}
