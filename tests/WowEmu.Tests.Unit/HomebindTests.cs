using WowEmu.Core;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Where a character comes home to.
/// </summary>
/// <remarks>
/// Distinct from the graveyard a ghost releases to: the graveyard is wherever you happened to die,
/// and this is somewhere you chose by speaking to an innkeeper.
/// </remarks>
public sealed class HomebindTests
{
    /// <summary>A homebind carries a map, an area and a position.</summary>
    [Fact]
    public void AHomebind_CarriesWhereAndOnWhatMap()
    {
        Homebind bind = new(0, 87, new Position(-9464f, 62f, 56f, 0f));

        Assert.Equal(0u, bind.MapId);
        Assert.Equal(87u, bind.AreaId);
        Assert.Equal(-9464f, bind.Position.X);
    }

    /// <summary>
    /// It is the AREA, not the zone.
    /// </summary>
    /// <remarks>
    /// Upstream's column is called <c>zoneId</c> and holds an area, which is exactly the kind of
    /// name that gets believed. The client labels the hearthstone from it — a zone binds you to
    /// "Elwynn Forest" where the innkeeper is standing in Goldshire.
    /// </remarks>
    [Fact]
    public void TheAreaIsStored_NotTheZone()
    {
        Player player = InventoryFixture.Player();

        player.AreaId = Goldshire;
        player.ZoneId = ElwynnForest;
        player.Position = new Position(-9464f, 62f, 56f, 0f);

        // What the innkeeper handler records.
        player.Homebind = new Homebind(player.MapId, player.AreaId, player.Position);

        Assert.Equal(Goldshire, player.Homebind.AreaId);
        Assert.NotEqual(ElwynnForest, player.Homebind.AreaId);
    }

    /// <summary>Binding again moves it.</summary>
    [Fact]
    public void BindingAgain_MovesIt()
    {
        Player player = InventoryFixture.Player();

        player.Homebind = new Homebind(0, Goldshire, new Position(-9464f, 62f, 56f, 0f));
        player.Homebind = new Homebind(1, Orgrimmar, new Position(1600f, -4400f, 31f, 0f));

        Assert.Equal(1u, player.Homebind.MapId);
        Assert.Equal(Orgrimmar, player.Homebind.AreaId);
    }

    /// <summary>
    /// A character who has never bound anywhere has no saved row, not a row of zeroes.
    /// </summary>
    /// <remarks>
    /// Zero is a real map id — Eastern Kingdoms — so a row of zeroes is a valid-looking binding to
    /// the middle of the ocean rather than an absent one. Upstream keeps homebind in its own table
    /// for exactly this reason, and the starting point stands until something replaces it.
    /// </remarks>
    [Fact]
    public void NeverBound_IsAbsentRatherThanZero()
    {
        Player player = InventoryFixture.Player();

        // Nothing has set it, so it is the default — which a caller distinguishes by the row being
        // missing, not by inspecting the value.
        Assert.Equal(default, player.Homebind);
        Assert.Equal(0u, player.Homebind.MapId);
    }

    private const uint Goldshire = 87;
    private const uint ElwynnForest = 12;
    private const uint Orgrimmar = 1637;
}
