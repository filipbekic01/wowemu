using WowEmu.Data.Client;

namespace WowEmu.Tests.Unit;

/// <summary>
/// <c>AreaTable.dbc</c>: which zone a place belongs to, and which liquid it substitutes.
/// </summary>
/// <remarks>
/// Two things hang off this table and they are unrelated. The first is the area-to-zone mapping,
/// which everything keyed by zone needs — graveyards, the character list, the location display.
/// The second is the liquid override, four columns that let a zone replace the water the geometry
/// describes with its own.
/// </remarks>
public sealed class AreaTableTests
{
    // Elwynn Forest is a zone; Northshire Valley is a subzone of it. Tirisfal Glades and Deathknell
    // are the same relationship on the Horde side.
    private const uint ElwynnForest = 12;
    private const uint NorthshireValley = 9;
    private const uint TirisfalGlades = 85;
    private const uint Deathknell = 154;

    [RequiresClientDataFact]
    public void TheTableLoads()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(2307, stores.Areas.Count);
    }

    /// <summary>
    /// A subzone resolves to the zone that contains it, and a zone to itself.
    /// </summary>
    /// <remarks>
    /// The distinction the whole store exists for. A terrain chunk stores an <i>area</i>, and for a
    /// subzone that is a different number from the zone — so using one where the other is wanted
    /// works everywhere a zone has no subzones and fails silently everywhere it does. Graveyard
    /// lookup is keyed by zone, which is how a ghost in Northshire found no graveyard at all.
    /// </remarks>
    [RequiresClientDataTheory]
    [InlineData(NorthshireValley, ElwynnForest)]
    [InlineData(Deathknell, TirisfalGlades)]
    [InlineData(ElwynnForest, ElwynnForest)]
    [InlineData(TirisfalGlades, TirisfalGlades)]
    public void ASubzone_ResolvesToItsZone(uint area, uint expectedZone)
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(expectedZone, stores.ZoneFor(area));
    }

    /// <summary>An area nobody has heard of is treated as its own zone.</summary>
    /// <remarks>
    /// Answering zero would be the other option and is worse: zero disables everything keyed by
    /// zone, silently, for whatever unknown place the player is standing in.
    /// </remarks>
    [RequiresClientDataFact]
    public void AnUnknownArea_IsItsOwnZone()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(999999u, stores.ZoneFor(999999));
    }

    /// <summary>The rows carry the names and parents the format string claims.</summary>
    /// <remarks>
    /// A format string one character out shifts every column after it and produces values that are
    /// wrong without being obviously so — a plausible id, a plausible level. Naming a couple of
    /// known rows is what catches that.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheColumns_LineUpWithTheFormatString()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.True(stores.Areas.TryGet(ElwynnForest, out AreaTableEntry? elwynn));
        Assert.Equal("Elwynn Forest", elwynn!.Name);
        Assert.Equal(0u, elwynn.MapId);
        Assert.True(elwynn.IsZone);

        Assert.True(stores.Areas.TryGet(NorthshireValley, out AreaTableEntry? northshire));
        Assert.Equal("Northshire Valley", northshire!.Name);
        Assert.Equal(ElwynnForest, northshire.ParentZoneId);
        Assert.False(northshire.IsZone);
    }

    /// <summary>
    /// Naxxramas substitutes its own slime for the generic kind.
    /// </summary>
    /// <remarks>
    /// The reason the override exists, and the one place in the shipped data it visibly matters.
    /// Only five of the 2,307 areas override anything at all, and <b>none of them has any terrain
    /// liquid</b> — every one is an instance whose water lives inside a WMO. So an override applied
    /// to the terrain path alone would be dead code; it has to reach the model path.
    /// </remarks>
    [RequiresClientDataFact]
    public void Naxxramas_SubstitutesItsOwnSlime()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        const uint Naxxramas = 3456;
        const uint SlimeSoundBank = 3;
        const uint NaxxramasSlime = 21;

        Assert.True(stores.Areas.TryGet(Naxxramas, out AreaTableEntry? area));
        Assert.Equal(NaxxramasSlime, area!.OverrideFor(SlimeSoundBank));

        // And it overrides nothing else: a zone names one replacement per liquid kind.
        Assert.Equal(0u, area.OverrideFor(0));
    }

    /// <summary>
    /// The generic liquids are overridable and the specific ones are not.
    /// </summary>
    /// <remarks>
    /// Upstream's bare <c>&lt; 21</c>. Entry 21 <i>is</i> Naxxramas' slime — the thing an override
    /// produces — so letting it be overridden in turn would let a zone replace the liquid it just
    /// asked for.
    /// </remarks>
    [RequiresClientDataFact]
    public void OnlyTheGenericLiquids_AreOverridable()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        // The four generic ones sit below the threshold.
        foreach (uint generic in new uint[] { 1, 2, 3, 20 })
        {
            Assert.True(stores.LiquidTypes.TryGet(generic, out _), $"entry {generic} should exist");
            Assert.True(generic < WorldLiquid.FirstSpecificLiquid);
        }

        // And the thing an override produces sits at or above it.
        Assert.True(WorldLiquid.FirstSpecificLiquid <= 21);
    }

    /// <summary>
    /// A point inside Naxxramas comes back as Naxxramas' own slime.
    /// </summary>
    /// <remarks>
    /// The end-to-end check, through the model path where the water actually is. The unit tests
    /// above prove the table says the right thing; this proves the override is reached.
    /// </remarks>
    [RequiresMapsFact]
    public void InsideNaxxramas_TheSlimeIsOverridden()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        TerrainMap terrain = new TerrainManager(ClientData.DataDirectory).GetMap(NaxxramasMapId);
        StaticMapTree vmaps = new VmapManager(ClientData.DataDirectory).GetMap(NaxxramasMapId);

        LiquidData liquid = WorldLiquid.Get(
            terrain, vmaps, NaxxramasX, NaxxramasY, NaxxramasZ, 2.0f, stores.LiquidTypes, stores.Areas);

        Assert.Equal(LiquidStatus.InWater, liquid.Status);
        Assert.Equal(LiquidTypeMask.Slime, liquid.Type);

        // 20 is "WMO Slime", the generic kind the geometry stores; 21 is "Naxxramas - Slime".
        Assert.Equal(21u, liquid.Entry);

        // Without the area table the geometry's own kind stands, which is what makes the difference
        // above attributable to the override rather than to the model.
        LiquidData unresolved = WorldLiquid.Get(
            terrain, vmaps, NaxxramasX, NaxxramasY, NaxxramasZ, 2.0f, stores.LiquidTypes);

        Assert.Equal(20u, unresolved.Entry);
    }

    // A slime channel inside the Naxxramas WMO, found by walking the model's own liquid grid.
    private const uint NaxxramasMapId = 533;
    private const float NaxxramasX = 3107.8652f;
    private const float NaxxramasY = -3536.4414f;
    private const float NaxxramasZ = 284.65808f;
}
