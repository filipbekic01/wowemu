using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// A higher rank replacing a lower one.
/// </summary>
/// <remarks>
/// Every rank stayed in the book before this, all castable — and the weaker one is the one that
/// stays where the player's finger already is, so they keep casting it.
/// <para>
/// The chains come from a curated world table. Nothing in the client's own data says that Fireball
/// rank 3 supersedes rank 2.
/// </para>
/// </remarks>
public sealed class SpellRankTests
{
    /// <summary>A higher rank deactivates the lower one, and both stay in the book.</summary>
    /// <remarks>
    /// Deactivated rather than removed, following upstream: removing loses the fact that the
    /// character ever had it, which matters the moment an unlearn has to put it back.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task AHigherRank_DeactivatesTheLowerAsync()
    {
        Player player = await RankedPlayerAsync();

        player.Spells.Learn(FireballRank1);
        Assert.True(player.Spells.IsActive(FireballRank1));

        player.Spells.Learn(FireballRank2);

        Assert.True(player.Spells.Knows(FireballRank1));
        Assert.False(player.Spells.IsActive(FireballRank1));
        Assert.True(player.Spells.IsActive(FireballRank2));
    }

    /// <summary>
    /// Learning a lower rank than one already known deactivates the NEW spell.
    /// </summary>
    /// <remarks>
    /// The direction that is easy to miss. It happens whenever a trainer's list is worked through
    /// out of order, and getting it backwards silently downgrades the character — they keep the
    /// spell they paid for and lose the one they had.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task ALowerRank_DeactivatesItselfAsync()
    {
        Player player = await RankedPlayerAsync();

        player.Spells.Learn(FireballRank2);
        player.Spells.Learn(FireballRank1);

        Assert.True(player.Spells.IsActive(FireballRank2));
        Assert.False(player.Spells.IsActive(FireballRank1));
    }

    /// <summary>
    /// Ranks of different spells do not supersede each other.
    /// </summary>
    /// <remarks>
    /// Comparing ranks without checking the chain makes Fireball rank 3 supersede Frostbolt rank 2,
    /// since both are just "a rank". The chain is half the condition.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task DifferentSpells_DoNotSupersedeEachOtherAsync()
    {
        SpellRankStore ranks = await LoadAsync();

        // Both are genuinely ranked and in different chains — otherwise the assertions below would
        // hold for the wrong reason, since an unranked spell supersedes nothing either.
        Assert.True(ranks.IsRanked(FrostboltRank2));
        Assert.True(ranks.IsRanked(FireballRank1));
        Assert.NotEqual(ranks.FirstOf(FrostboltRank2), ranks.FirstOf(FireballRank1));
        Assert.Equal(2, ranks.RankOf(FrostboltRank2));

        Assert.False(ranks.Supersedes(FrostboltRank2, FireballRank1));
        Assert.False(ranks.Supersedes(FireballRank2, FrostboltRank1));
        Assert.True(ranks.Supersedes(FireballRank2, FireballRank1));
    }

    /// <summary>An unranked spell supersedes nothing and is never superseded.</summary>
    /// <remarks>
    /// Most of the spell table. Treating an unranked spell as rank 0 of some chain would have every
    /// one of them fighting every other.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task AnUnrankedSpell_StandsAloneAsync()
    {
        SpellRankStore ranks = await LoadAsync();

        Assert.False(ranks.IsRanked(SpellBook.DualWieldSpell));
        Assert.Equal(0, ranks.RankOf(SpellBook.DualWieldSpell));
        Assert.Equal(SpellBook.DualWieldSpell, ranks.FirstOf(SpellBook.DualWieldSpell));
        Assert.Empty(ranks.ChainOf(SpellBook.DualWieldSpell));
    }

    /// <summary>
    /// Restoring a saved book settles the ranks whatever order the rows arrive in.
    /// </summary>
    /// <remarks>
    /// The database returns them in no useful order, so settling each as it lands would deactivate
    /// a high rank that happened to be read before its own lower ones — and the character would log
    /// in having quietly lost their best spell.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task Restoring_SettlesRanksWhateverTheOrderAsync()
    {
        Player player = await RankedPlayerAsync();

        // Deliberately highest first, which is the order that breaks a naive implementation.
        player.Spells.Restore([FireballRank3, FireballRank2, FireballRank1]);

        Assert.True(player.Spells.IsActive(FireballRank3));
        Assert.False(player.Spells.IsActive(FireballRank2));
        Assert.False(player.Spells.IsActive(FireballRank1));
    }

    /// <summary>The real table loads, with the chain shape the data actually has.</summary>
    [RequiresWorldDatabaseFact]
    public async Task TheRealTable_LoadsAsync()
    {
        SpellRankStore ranks = await LoadAsync();

        Assert.Equal(3502, ranks.Count);
        Assert.Equal(598, ranks.ChainCount);

        // Fireball's chain, lowest rank first, starting from its own first spell.
        Assert.Equal(FireballRank1, ranks.FirstOf(FireballRank3));
        Assert.Equal(1, ranks.RankOf(FireballRank1));
        Assert.Equal(3, ranks.RankOf(FireballRank3));
        Assert.Equal(FireballRank1, ranks.ChainOf(FireballRank3)[0]);
    }

    /// <summary>With no rank table at all, every rank stays active.</summary>
    /// <remarks>
    /// So that everything not needing a database carries on working, rather than spells silently
    /// deactivating each other because a store was not wired up.
    /// </remarks>
    [Fact]
    public void WithNoRankTable_EveryRankStaysActive()
    {
        Player player = InventoryFixture.Player();

        player.Spells.Learn(FireballRank1);
        player.Spells.Learn(FireballRank2);

        Assert.True(player.Spells.IsActive(FireballRank1));
        Assert.True(player.Spells.IsActive(FireballRank2));
    }

    // Fireball ranks 1-3 and Frostbolt ranks 1-2, from the real table.
    private const uint FireballRank1 = 133;
    private const uint FireballRank2 = 143;
    private const uint FireballRank3 = 145;
    private const uint FrostboltRank1 = 116;
    private const uint FrostboltRank2 = 205;

    private static async Task<SpellRankStore> LoadAsync()
    {
        SpellRankStore ranks = new();

        await ranks.LoadAsync(WorldDatabase.ConnectionString);

        return ranks;
    }

    private static async Task<Player> RankedPlayerAsync()
    {
        Player player = InventoryFixture.Player();
        player.Spells.Ranks = await LoadAsync();

        return player;
    }
}
