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
    public static LiquidData Get(
        TerrainMap? terrain,
        StaticMapTree? vmaps,
        float x,
        float y,
        float z,
        float collisionHeight,
        DbcStore<LiquidTypeEntry>? liquidTypes = null)
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
            return found;
        }

        LiquidData ground = terrain.GetLiquidData(x, y, z, collisionHeight);

        // Never downgrade a model's answer to "no water" — but do let real terrain water take over,
        // provided its surface clears the model floor the point was found standing on.
        if (ground.Status != LiquidStatus.NoWater && ground.Level > modelFloor)
        {
            return ground;
        }

        return found;
    }
}
