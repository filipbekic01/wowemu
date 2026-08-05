namespace WowEmu.Data.Client;

/// <summary>
/// The height of whatever a point is standing on, from terrain and models together.
/// </summary>
/// <remarks>
/// Two sources answer the same question and neither is complete. Terrain knows the ground and
/// nothing else; vmaps know buildings, bridges and platforms and nothing about the ground beneath
/// them. A player on a bridge over a valley is 40 yards above the terrain and standing on a model;
/// a player in an open field is on terrain with no model anywhere near.
/// <para>
/// So the floor is the <b>higher</b> of the two, and either may legitimately be absent.
/// </para>
/// </remarks>
public static class WorldHeight
{
    /// <summary>
    /// How far above a point to start looking for a model surface.
    /// </summary>
    /// <remarks>
    /// A downward ray from exactly the point would miss the floor a player is standing on when
    /// rounding puts them a hair below it. Starting slightly above costs nothing and removes a
    /// whole class of intermittent wrong answers.
    /// </remarks>
    public const float SearchAbove = 2.0f;

    /// <summary>How far below to keep looking. Beyond this a surface is not what you are on.</summary>
    public const float SearchBelow = 50.0f;

    /// <summary>
    /// The surface under a point, or null when neither source knows of one.
    /// </summary>
    /// <remarks>
    /// Null is a real answer and not a failure: it means a hole in the terrain, an unloaded tile, or
    /// genuinely open sky. A caller that treats null as "the floor is at zero" will conclude that
    /// every player over a terrain hole is flying.
    /// </remarks>
    public static float? GetFloor(
        TerrainMap? terrain,
        StaticMapTree? vmaps,
        float x,
        float y,
        float z)
    {
        float? terrainHeight = null;

        if (terrain is not null)
        {
            float height = terrain.GetHeight(x, y);

            // The terrain reader reports a hole or a missing tile with a sentinel, not an exception.
            if (height > MapGeometry.InvalidHeight)
            {
                terrainHeight = height;
            }
        }

        float? modelHeight = vmaps?.GetHeight(x, y, z + SearchAbove, SearchBelow + SearchAbove);

        return (terrainHeight, modelHeight) switch
        {
            (null, null) => null,
            (float t, null) => t,
            (null, float m) => m,
            (float t, float m) => MathF.Max(t, m),
        };
    }
}
