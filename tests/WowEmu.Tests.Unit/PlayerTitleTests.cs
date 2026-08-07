using WowEmu.Data.Client;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Titles: the 128-bit mask of what is earned, and the one that is worn.
/// </summary>
public sealed class PlayerTitleTests
{
    /// <summary>
    /// A title id is not the bit that gets set.
    /// </summary>
    /// <remarks>
    /// A quest names a <c>CharTitles.dbc</c> id; the bit index is a separate column of the same
    /// row. Setting the bit for the id grants some other title entirely, and the difference only
    /// shows up as a player wearing something they never earned.
    /// </remarks>
    [RequiresClientDataFact]
    public void ATitleId_IsNotItsBit()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        Player player = Character();

        // The two columns agree for the first few dozen rows, so the test has to pick a row where
        // they genuinely differ — otherwise it passes against the id just as happily.
        CharTitleEntry title = stores.CharTitles.Entries.First(entry => entry.BitIndex != entry.Id);

        player.Titles.Learn(title.Id, stores.CharTitles);

        Assert.True(player.Titles.HasByBit(title.BitIndex));
        Assert.False(player.Titles.HasByBit(title.Id));
    }

    /// <summary>
    /// Every title in the file fits the mask the client carries.
    /// </summary>
    /// <remarks>
    /// <b>The mask is 192 bits, not 128.</b> The file carries bit indices up to 142, so a 128-bit
    /// reading drops fifteen real titles — they read as unearned forever and nothing says so. This
    /// is what caught that.
    /// </remarks>
    [RequiresClientDataFact]
    public void EveryTitle_FitsTheMask()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.NotEmpty(stores.CharTitles.Entries);
        Assert.All(stores.CharTitles.Entries, title => Assert.True(title.BitIndex < PlayerTitles.Count));
    }

    /// <summary>
    /// A title past the first thirty-two lands in the right field.
    /// </summary>
    /// <remarks>
    /// The mask spans four consecutive fields. Writing them all to the first sets a bit for some
    /// other title and leaves the real one unset — and the first thirty-two titles work fine
    /// throughout, so the bug hides behind everything an early character earns.
    /// </remarks>
    [Fact]
    public void ATitlePastThirtyTwo_LandsInTheRightField()
    {
        Player player = Character();

        player.Titles.LearnByBit(100);

        Assert.True(player.Titles.HasByBit(100));
        Assert.False(player.Titles.HasByBit(100 - 32));
        Assert.False(player.Titles.HasByBit(100 - 64));
        Assert.False(player.Titles.HasByBit(100 - 96));
    }

    /// <summary>An unearned title cannot be worn.</summary>
    /// <remarks>
    /// The client only sends what it drew, so a request for one the character never earned came
    /// from somewhere else.
    /// </remarks>
    [Fact]
    public void AnUnearnedTitle_CannotBeWorn()
    {
        Player player = Character();

        Assert.False(player.Titles.Wear(40));
        Assert.Equal(0u, player.Titles.Chosen);

        player.Titles.LearnByBit(40);

        Assert.True(player.Titles.Wear(40));
        Assert.Equal(40u, player.Titles.Chosen);
    }

    /// <summary>Taking a title away takes it off too.</summary>
    /// <remarks>
    /// Leaving the worn field set displays a title the character no longer has, and the client has
    /// no reason to doubt it.
    /// </remarks>
    [Fact]
    public void RemovingTheWornTitle_TakesItOff()
    {
        Player player = Character();

        player.Titles.LearnByBit(40);
        player.Titles.Wear(40);

        player.Titles.Remove(40);

        Assert.Equal(0u, player.Titles.Chosen);
        Assert.False(player.Titles.HasByBit(40));
    }

    /// <summary>Everything earned survives a save and load.</summary>
    [Fact]
    public void EarnedTitles_SurviveARoundTrip()
    {
        Player saved = Character();

        saved.Titles.LearnByBit(4);
        saved.Titles.LearnByBit(100);
        saved.Titles.Wear(100);

        string stored = string.Join(' ', saved.Titles.Known);

        Player loaded = Character();

        foreach (string part in stored.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            loaded.Titles.LearnByBit(uint.Parse(part, System.Globalization.CultureInfo.InvariantCulture));
        }

        loaded.Titles.Wear(saved.Titles.Chosen);

        Assert.Equal([4u, 100u], loaded.Titles.Known);
        Assert.Equal(100u, loaded.Titles.Chosen);
    }

    private static Player Character() => InventoryFixture.Player(level: 20, proficiencies: false);
}
