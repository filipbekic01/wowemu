namespace WowEmu.Data.Client;

/// <summary>Liquid found inside a model, and whether that model encloses the point.</summary>
/// <param name="Liquid">The water itself.</param>
/// <param name="IsInterior">
/// Whether the group is an enclosed interior. It decides whether terrain liquid may override this.
/// </param>
public readonly record struct ModelLiquid(LiquidData Liquid, bool IsInterior);

/// <summary>
/// The liquid at a point, from terrain and models together.
/// </summary>
/// <remarks>
/// The same two-source shape as <see cref="WorldHeight"/>, and for the same reason: neither knows
/// the whole world. Terrain liquid is every lake, river and ocean; a WMO's own liquid grid is every
/// fountain, canal, moat and flooded dungeon room. Reading only one of them leaves a whole category
/// of water invisible.
/// <para>
/// <b>They do not combine by taking the higher one.</b> Height does, because a bridge over a valley
/// is genuinely above the ground. Liquid does not, because a building standing in a lake would
/// otherwise have the lake running through its ground floor. The rule is upstream's: a model's
/// liquid wins outright when the point is in an enclosed interior, and otherwise terrain liquid may
/// take over — but only where its surface is above the model's floor, which is what stops a lake
/// outside a doorway flooding the room behind it.
/// </para>
/// </remarks>
public static class WorldLiquid
{
    /// <summary>How far below the floor a point may be and still count as in the liquid.</summary>
    /// <remarks>
    /// <c>GROUND_HEIGHT_TOLERANCE</c>. The floor and the liquid come from different sources that
    /// disagree slightly, and without the slack a player standing on the bottom of a pool flickers
    /// between swimming and not.
    /// </remarks>
    public const float GroundTolerance = 0.05f;

    /// <summary>
    /// The liquid at a point, or <see cref="LiquidData.None"/> where there is none.
    /// </summary>
    /// <param name="terrain">The map's terrain. Null skips terrain liquid entirely.</param>
    /// <param name="vmaps">The map's static collision. Null skips model liquid entirely.</param>
    /// <param name="collisionHeight">
    /// How tall the unit is — the depth at which it stops wading and is submerged.
    /// </param>
    /// <param name="liquidTypes">
    /// <c>LiquidType.dbc</c>, which is what classifies a model's liquid. Optional, and without it a
    /// model's water arrives with no type — see <see cref="StaticMapTree.GetLiquid"/>. Terrain
    /// liquid is unaffected either way, because the extractor resolved its type at extraction time.
    /// </param>
    /// <param name="areas">
    /// <c>AreaTable.dbc</c>, for the zone liquid override. Optional; without it a zone that
    /// substitutes its own liquid is not noticed and the geometry's own kind stands.
    /// </param>
    public static LiquidData Get(
        TerrainMap? terrain,
        StaticMapTree? vmaps,
        float x,
        float y,
        float z,
        float collisionHeight,
        DbcStore<LiquidTypeEntry>? liquidTypes = null,
        DbcStore<AreaTableEntry>? areas = null)
    {
        ModelLiquid? model = vmaps?.GetLiquid(x, y, z, collisionHeight, liquidTypes);

        LiquidData found = LiquidData.None;
        bool useTerrain = true;
        float modelFloor = float.NegativeInfinity;

        if (model is { } indoor)
        {
            // Inside an enclosed group the model's water is the only water. Outside one — under an
            // archway, on a jetty — the terrain still gets its say.
            useTerrain = !indoor.IsInterior;
            modelFloor = indoor.Liquid.FloorLevel;

            // The surface has to be above the floor it was found over, and the point at or above
            // that floor. A model whose liquid sits below its own floor is geometry the point is
            // not in contact with.
            if (indoor.Liquid.Level > indoor.Liquid.FloorLevel && z >= indoor.Liquid.FloorLevel - GroundTolerance)
            {
                found = indoor.Liquid;
            }
            else
            {
                // Upstream still reports a status here even when it refuses the liquid itself, so
                // that a caller can tell "above a pool I am not in" from "no pool at all".
                found = LiquidData.None with { Status = indoor.Liquid.Status };
            }
        }

        if (!useTerrain || terrain is null)
        {
            return Override(found, terrain, x, y, liquidTypes, areas);
        }

        LiquidData ground = terrain.GetLiquidData(x, y, z, collisionHeight);

        // Never downgrade a model's answer to "no water" — but do let real terrain water take over,
        // provided its surface clears the model floor the point was found standing on.
        if (ground.Status != LiquidStatus.NoWater && ground.Level > modelFloor)
        {
            return Override(ground, terrain, x, y, liquidTypes, areas);
        }

        return Override(found, terrain, x, y, liquidTypes, areas);
    }

    /// <summary>
    /// Lets the zone substitute its own liquid for the one the geometry describes.
    /// </summary>
    /// <remarks>
    /// Port of the override branch shared by <c>GridTerrainData::GetLiquidData</c> and
    /// <c>Map::GetLiquidData</c>. This is how Naxxramas gets slime where the terrain says water, and
    /// how Outland's lakes read differently from Azeroth's.
    /// <para>
    /// <b>Only entries below 21 are overridable.</b> Those are the generic ones — plain water, ocean,
    /// magma, slime — and everything above is already a specific named liquid that a zone has no
    /// business replacing. Dropping the test lets a zone override the very liquid it defined.
    /// </para>
    /// <para>
    /// The subzone is asked first and the zone only if it declines, which is upstream's order: a
    /// subzone may override where its parent does not.
    /// </para>
    /// <para>
    /// Note what happens to the type bits: everything but dark water is discarded and rebuilt from
    /// the sound bank. Dark water is a property of the liquid rather than a kind of it, so it
    /// survives an override that changes what the liquid is.
    /// </para>
    /// </remarks>
    private static LiquidData Override(
        LiquidData liquid,
        TerrainMap? terrain,
        float x,
        float y,
        DbcStore<LiquidTypeEntry>? liquidTypes,
        DbcStore<AreaTableEntry>? areas)
    {
        if (liquid.Status == LiquidStatus.NoWater || liquidTypes is null)
        {
            return liquid;
        }

        if (!liquidTypes.TryGet(liquid.Entry, out LiquidTypeEntry? entry) || entry is null)
        {
            // An entry the DBC does not describe keeps whatever the geometry said about it.
            return liquid;
        }

        uint resolvedEntry = liquid.Entry;
        uint soundBank = entry.SoundBank;

        if (liquid.Entry < FirstSpecificLiquid && terrain is not null && areas is not null)
        {
            uint replacement = ZoneOverride(areas, terrain.GetAreaId(x, y), soundBank);

            if (replacement != 0
                && liquidTypes.TryGet(replacement, out LiquidTypeEntry? replaced)
                && replaced is not null)
            {
                resolvedEntry = replacement;
                soundBank = replaced.SoundBank;
            }
        }

        LiquidTypeMask type = (liquid.Type & LiquidTypeMask.DarkWater) | SoundBankMask(soundBank);

        return liquid with { Entry = resolvedEntry, Type = type };
    }

    /// <summary>
    /// The first liquid entry a zone may not override.
    /// </summary>
    /// <remarks>
    /// Upstream's bare <c>&lt; 21</c>. Below it are the generic liquids every zone shares; at and
    /// above it are the specific ones — entry 21 is Naxxramas' own slime, which exists precisely
    /// because a zone overrode something to reach it.
    /// </remarks>
    public const uint FirstSpecificLiquid = 21;

    /// <summary>The zone's replacement for a liquid kind, asking the subzone before its parent.</summary>
    private static uint ZoneOverride(DbcStore<AreaTableEntry> areas, ushort areaId, uint soundBank)
    {
        if (areaId == 0 || !areas.TryGet(areaId, out AreaTableEntry? area) || area is null)
        {
            return 0;
        }

        uint replacement = area.OverrideFor(soundBank);

        if (replacement != 0 || area.ParentZoneId == 0)
        {
            return replacement;
        }

        return areas.TryGet(area.ParentZoneId, out AreaTableEntry? zone) && zone is not null
            ? zone.OverrideFor(soundBank)
            : 0;
    }

    /// <summary>The <c>MAP_LIQUID_TYPE_*</c> bit for a sound bank. <c>1 &lt;&lt; type</c>.</summary>
    private static LiquidTypeMask SoundBankMask(uint soundBank) => soundBank switch
    {
        0 => LiquidTypeMask.Water,
        1 => LiquidTypeMask.Ocean,
        2 => LiquidTypeMask.Magma,
        3 => LiquidTypeMask.Slime,
        _ => LiquidTypeMask.None,
    };
}
