using WowEmu.Data.Client;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Talents: the point economy, the tree rules, and what a reset costs.
/// </summary>
public sealed class TalentTests
{
    // ------------------------------------------------------------------ the point economy

    /// <summary>
    /// Nothing below level 10, then one point per level.
    /// </summary>
    /// <remarks>
    /// <b>A naive <c>level - 9</c> goes negative below 10</b> and, unsigned, hands a level-1
    /// character four billion talent points.
    /// </remarks>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(60, 51)]
    [InlineData(80, 71)]
    public void TalentPoints_StartAtTen(byte level, uint expected) =>
        Assert.Equal(expected, PlayerTalents.PointsForLevel(level));

    /// <summary>
    /// A talent at rank 0 is one point spent, not zero.
    /// </summary>
    /// <remarks>
    /// Ranks are stored zero-based because that is what the client speaks. Summing them raw
    /// undercounts every tree by the number of talents in it, which unlocks deeper rows late.
    /// </remarks>
    [RequiresClientDataFact]
    public void ARankZeroTalent_CostsOnePoint()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores);

        player.Talents.FreePoints = 10;

        Assert.Equal(
            TalentResult.Ok,
            player.Talents.Learn(talent.Id, 0, stores.Talents, stores.TalentTabs, out _));

        Assert.Equal(1u, player.Talents.TotalSpent());
        Assert.Equal(9u, player.Talents.FreePoints);
    }

    /// <summary>
    /// Learning rank 3 from nothing costs three points, not one.
    /// </summary>
    /// <remarks>
    /// The client sends the rank it wants rather than one increment. Charging a point per click
    /// lets a player fill a tree for a fraction of its cost, and the pane looks correct throughout.
    /// </remarks>
    [RequiresClientDataFact]
    public void LearningADeepRank_CostsEveryRankBelowIt()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores, minRanks: 3);

        player.Talents.FreePoints = 10;

        Assert.Equal(
            TalentResult.Ok,
            player.Talents.Learn(talent.Id, 2, stores.Talents, stores.TalentTabs, out _));

        Assert.Equal(7u, player.Talents.FreePoints);
    }

    /// <summary>And topping a talent up charges only the difference.</summary>
    [RequiresClientDataFact]
    public void ToppingUp_ChargesOnlyTheDifference()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores, minRanks: 3);

        player.Talents.FreePoints = 10;

        player.Talents.Learn(talent.Id, 0, stores.Talents, stores.TalentTabs, out _);
        player.Talents.Learn(talent.Id, 2, stores.Talents, stores.TalentTabs, out _);

        Assert.Equal(7u, player.Talents.FreePoints);
    }

    /// <summary>A rank already known is refused rather than charged again.</summary>
    [RequiresClientDataFact]
    public void AKnownRank_IsRefused()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores);

        player.Talents.FreePoints = 10;

        player.Talents.Learn(talent.Id, 0, stores.Talents, stores.TalentTabs, out _);

        Assert.Equal(
            TalentResult.AlreadyKnown,
            player.Talents.Learn(talent.Id, 0, stores.Talents, stores.TalentTabs, out _));

        Assert.Equal(9u, player.Talents.FreePoints);
    }

    /// <summary>Without the points, nothing is learned.</summary>
    [RequiresClientDataFact]
    public void WithoutPoints_NothingIsLearned()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores);

        player.Talents.FreePoints = 0;

        Assert.Equal(
            TalentResult.NotEnoughPoints,
            player.Talents.Learn(talent.Id, 0, stores.Talents, stores.TalentTabs, out _));
    }

    // ------------------------------------------------------------------ the tree rules

    /// <summary>
    /// A warrior cannot learn a mage talent.
    /// </summary>
    /// <remarks>
    /// The client greys out other classes' trees, so nothing legitimate ever asks — which is
    /// exactly why the server has to check. A modified client asks for whatever it likes.
    /// </remarks>
    [RequiresClientDataFact]
    public void AnotherClassesTalent_IsRefused()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, _) = Warrior(stores);

        player.Talents.FreePoints = 50;

        // A talent from a tree the warrior's class mask does not cover.
        TalentEntry foreign = stores.Talents.Entries.First(
            entry => stores.TalentTabs.TryGet(entry.TabId, out TalentTabEntry? tab)
                && tab is not null
                && (tab.ClassMask & PlayerTalents.ClassMaskOf(1)) == 0);

        Assert.Equal(
            TalentResult.WrongClass,
            player.Talents.Learn(foreign.Id, 0, stores.Talents, stores.TalentTabs, out _));
    }

    /// <summary>
    /// A deeper row needs five points per row already spent in that tree.
    /// </summary>
    /// <remarks>
    /// <b>A row is worth five points regardless of how many talents sit on it.</b> Row 3 wants
    /// fifteen — not three talents taken, and not fifteen spent anywhere.
    /// </remarks>
    [RequiresClientDataFact]
    public void ADeeperRow_NeedsFivePointsPerRow()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry first) = Warrior(stores);

        // A talent on row 1 of the same tree, with no prerequisite of its own.
        TalentEntry deeper = stores.Talents.Entries.First(
            entry => entry.TabId == first.TabId && entry.Row == 1 && entry.DependsOnTalent == 0);

        player.Talents.FreePoints = 50;

        Assert.Equal(
            TalentResult.RowLocked,
            player.Talents.Learn(deeper.Id, 0, stores.Talents, stores.TalentTabs, out _));

        // Five points into row 0 of the same tree opens it.
        SpendInRowZero(player, stores, first.TabId, 5);

        Assert.Equal(
            TalentResult.Ok,
            player.Talents.Learn(deeper.Id, 0, stores.Talents, stores.TalentTabs, out _));
    }

    /// <summary>
    /// Points spent in another tree do not unlock this one's rows.
    /// </summary>
    /// <remarks>
    /// The obvious mistake is to count every point spent. That would let a player reach the bottom
    /// of one tree by filling another, which is the whole of what a talent tree is not.
    /// </remarks>
    [RequiresClientDataFact]
    public void PointsInAnotherTree_DoNotUnlockThisOne()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry first) = Warrior(stores);

        uint otherTab = stores.Talents.Entries
            .First(entry => entry.TabId != first.TabId
                && stores.TalentTabs.TryGet(entry.TabId, out TalentTabEntry? tab)
                && tab is not null
                && (tab.ClassMask & PlayerTalents.ClassMaskOf(1)) != 0).TabId;

        player.Talents.FreePoints = 50;
        SpendInRowZero(player, stores, otherTab, 5);

        TalentEntry deeper = stores.Talents.Entries.First(
            entry => entry.TabId == first.TabId && entry.Row == 1 && entry.DependsOnTalent == 0);

        Assert.Equal(
            TalentResult.RowLocked,
            player.Talents.Learn(deeper.Id, 0, stores.Talents, stores.TalentTabs, out _));
    }

    /// <summary>
    /// A talent with a prerequisite needs its parent taken far enough.
    /// </summary>
    /// <remarks>
    /// The comparison is "at or above", as upstream's loop is — but <b>no real talent exercises the
    /// difference</b>. Cross-tabulating the file, every prerequisite asks for exactly the parent's
    /// <i>last</i> rank: (required 0, parent 1 rank) × 73, (1, 2) × 12, (2, 3) × 26, (4, 5) × 26.
    /// A prerequisite always means "max out the parent", so a parent can never be taken beyond what
    /// is asked, and an equality test would pass every test that can be built from this data. The
    /// comparison is kept because it is upstream's, not because anything here can tell them apart.
    /// </remarks>
    [RequiresClientDataFact]
    public void APrerequisite_NeedsTheParentMaxed()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        TalentEntry child = stores.Talents.Entries.First(
            entry => entry.DependsOnTalent != 0 && entry.DependsOnRank > 0);

        TalentEntry parentTalent = stores.Talents.Entries.First(e => e.Id == child.DependsOnTalent);

        Player player = PlayerOfClassFor(stores, child.TabId);
        player.Talents.FreePoints = 100;

        Assert.Equal(
            TalentResult.MissingPrerequisite,
            player.Talents.Learn(child.Id, 0, stores.Talents, stores.TalentTabs, out _));

        // One short of the requirement is still a refusal.
        player.Talents.Restore(0, parentTalent.Id, (byte)(child.DependsOnRank - 1));

        Assert.Equal(
            TalentResult.MissingPrerequisite,
            player.Talents.Learn(child.Id, 0, stores.Talents, stores.TalentTabs, out _));

        // At the requirement it is no longer the prerequisite standing in the way. It may still be
        // the row, which is a different refusal and a different fix.
        player.Talents.Restore(0, parentTalent.Id, (byte)child.DependsOnRank);

        Assert.NotEqual(
            TalentResult.MissingPrerequisite,
            player.Talents.Learn(child.Id, 0, stores.Talents, stores.TalentTabs, out _));
    }

    /// <summary>
    /// Every prerequisite in the file asks for the parent's last rank.
    /// </summary>
    /// <remarks>
    /// Pinned because it is what makes the test above unable to distinguish "at or above" from
    /// "exactly". If a future data set breaks the pattern, this is the test that says so — and the
    /// comparison suddenly starts mattering.
    /// </remarks>
    [RequiresClientDataFact]
    public void EveryPrerequisite_AsksForTheParentsLastRank()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        foreach (TalentEntry entry in stores.Talents.Entries)
        {
            if (entry.DependsOnTalent == 0
                || !stores.Talents.TryGet(entry.DependsOnTalent, out TalentEntry? parent)
                || parent is null)
            {
                continue;
            }

            Assert.Equal(parent.RankCount - 1, (int)entry.DependsOnRank);
        }
    }

    /// <summary>A rank the talent does not have is refused.</summary>
    /// <remarks>
    /// Talents have between one and five ranks, and the DBC's rank array is zero-padded — asking
    /// for rank 4 of a two-rank talent finds a spell id of zero, not a spell.
    /// </remarks>
    [RequiresClientDataFact]
    public void ARankTheTalentLacks_IsRefused()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        TalentEntry oneRank = stores.Talents.Entries.First(entry => entry.RankCount == 1);
        Player player = PlayerOfClassFor(stores, oneRank.TabId);

        player.Talents.FreePoints = 50;

        Assert.Equal(
            TalentResult.Unknown,
            player.Talents.Learn(oneRank.Id, 1, stores.Talents, stores.TalentTabs, out _));
    }

    // ------------------------------------------------------------------ resetting

    /// <summary>
    /// A reset gives back every point and takes away every rank's spell.
    /// </summary>
    /// <remarks>
    /// Every rank up to the one held, not just the held one — each rank is its own spell, and
    /// leaving the lower ones behind keeps a reset character's abilities working.
    /// </remarks>
    [RequiresClientDataFact]
    public void AReset_TakesAwayEveryRanksSpell()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores, minRanks: 3);

        player.Level = 60;
        player.Talents.FreePoints = 51;
        player.Talents.Learn(talent.Id, 2, stores.Talents, stores.TalentTabs, out _);

        IReadOnlyList<uint> removed = player.Talents.Reset(stores.Talents);

        Assert.Equal(3, removed.Count);
        Assert.Equal(51u, player.Talents.FreePoints);
        Assert.Empty(player.Talents.Active);
    }

    /// <summary>
    /// A reset leaves the other spec alone.
    /// </summary>
    /// <remarks>
    /// Wiping both would destroy the spec the player is not looking at, which they cannot notice
    /// until they switch to it.
    /// </remarks>
    [RequiresClientDataFact]
    public void AReset_LeavesTheOtherSpecAlone()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores);

        player.Talents.Restore(1, talent.Id, 0);
        player.Talents.FreePoints = 5;
        player.Talents.Learn(talent.Id, 0, stores.Talents, stores.TalentTabs, out _);

        player.Talents.Reset(stores.Talents);

        Assert.Empty(player.Talents.InSpec(0));
        Assert.Single(player.Talents.InSpec(1));
    }

    /// <summary>
    /// The reset cost climbs 1, 5, 10 gold and then in fives to fifty.
    /// </summary>
    [Theory]
    [InlineData(0u, 10000u)]
    [InlineData(10000u, 50000u)]
    [InlineData(50000u, 100000u)]
    [InlineData(100000u, 150000u)]
    [InlineData(450000u, 500000u)]
    [InlineData(500000u, 500000u)]
    public void TheResetCost_ClimbsAndCaps(uint last, uint expected) =>
        Assert.Equal(expected, TalentResetCost.Next(last, lastResetTime: 1000, now: 1000));

    /// <summary>
    /// And decays by five gold per whole month.
    /// </summary>
    /// <remarks>
    /// <b>The ladder is not monotonic.</b> Reading it as one makes respeccing permanently expensive
    /// after a few uses, which is the opposite of what the game does — a character who has not
    /// respecced in a year pays ten gold again.
    /// </remarks>
    [Fact]
    public void TheResetCost_DecaysWithTime()
    {
        const long month = TalentResetCost.MonthSeconds;

        // 50 gold, one month later: down five gold.
        Assert.Equal(450000u, TalentResetCost.Next(500000, 0, month));

        // Ten months later: floored at ten gold, not zero and not negative.
        Assert.Equal(
            TalentResetCost.Floor, TalentResetCost.Next(500000, 0, 10 * month));
    }

    // ------------------------------------------------------------------ glyphs

    /// <summary>
    /// The glyph unlock bits are not in level order.
    /// </summary>
    /// <remarks>
    /// <b>Level 30 unlocks bit 0x08 and level 50 unlocks 0x04.</b> The pane's layout and the unlock
    /// order genuinely disagree; assigning them in ascending order gives players the wrong sockets
    /// at the wrong levels, and the pane looks plausible either way.
    /// </remarks>
    [Fact]
    public void TheGlyphUnlocks_AreNotInLevelOrder()
    {
        Assert.Equal(0x03u, PlayerGlyphs.EnabledMaskFor(15));
        Assert.Equal(0x0Bu, PlayerGlyphs.EnabledMaskFor(30));
        Assert.Equal(0x0Fu, PlayerGlyphs.EnabledMaskFor(50));
        Assert.Equal(0x1Fu, PlayerGlyphs.EnabledMaskFor(70));
        Assert.Equal(0x3Fu, PlayerGlyphs.EnabledMaskFor(80));

        // The one that catches an ascending assignment: at 30 the fourth socket is open and the
        // third is not.
        Assert.Equal(0x08u, PlayerGlyphs.EnabledMaskFor(30) & 0x0Cu);
    }

    /// <summary>Below 15 there are no sockets at all.</summary>
    [Fact]
    public void BelowFifteen_ThereAreNoSockets() =>
        Assert.Equal(0u, PlayerGlyphs.EnabledMaskFor(14));

    /// <summary>
    /// A locked socket refuses a glyph.
    /// </summary>
    [RequiresClientDataFact]
    public void ALockedSocket_RefusesAGlyph()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        Player player = Character(level: 14);

        player.Glyphs.InitialiseForLevel(stores.GlyphSlots);

        GlyphPropertiesEntry glyph = stores.GlyphProperties.Entries.First();

        Assert.False(player.Glyphs.Set(0, glyph.Id, stores.GlyphProperties, stores.GlyphSlots));
    }

    /// <summary>
    /// A major glyph does not fit a minor socket.
    /// </summary>
    /// <remarks>
    /// Both carry a type mask and they have to agree. The client enforces it, which is why the
    /// server must — a modified one puts a major glyph in a minor socket for free power.
    /// </remarks>
    [RequiresClientDataFact]
    public void AGlyph_MustMatchItsSocket()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        Player player = Character(level: 80);

        player.Glyphs.InitialiseForLevel(stores.GlyphSlots);

        uint socketId = player.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_GLYPH_SLOTS_1);

        Assert.True(stores.GlyphSlots.TryGet(socketId, out GlyphSlotEntry? socket));
        Assert.NotNull(socket);

        GlyphPropertiesEntry wrongKind = stores.GlyphProperties.Entries
            .First(entry => entry.TypeFlags != socket.TypeFlags);
        GlyphPropertiesEntry rightKind = stores.GlyphProperties.Entries
            .First(entry => entry.TypeFlags == socket.TypeFlags);

        Assert.False(player.Glyphs.Set(0, wrongKind.Id, stores.GlyphProperties, stores.GlyphSlots));
        Assert.True(player.Glyphs.Set(0, rightKind.Id, stores.GlyphProperties, stores.GlyphSlots));
    }

    /// <summary>The sockets are written from the file, not invented.</summary>
    /// <remarks>
    /// Without them the pane has no sockets to draw at all, unlock mask or not.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheSockets_ComeFromTheFile()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        Player player = Character(level: 80);

        player.Glyphs.InitialiseForLevel(stores.GlyphSlots);

        for (int slot = 0; slot < PlayerGlyphs.SlotCount; slot++)
        {
            Assert.NotEqual(
                0u, player.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_GLYPH_SLOTS_1 + slot));
        }
    }

    // ------------------------------------------------------------------ dual spec

    /// <summary>
    /// The spec spells' base points are one less than the value they mean.
    /// </summary>
    /// <remarks>
    /// <b>A DBC-wide convention.</b> <c>EffectBasePoints</c> holds one less than the real figure,
    /// so the select spell's "spec 1 or 2" is stored as 0 or 1 — which happens to be the
    /// zero-based index this code wants, while the count spell's "1 or 2 specs" needs the +1 back.
    /// Applying the same adjustment to both gets one of them wrong.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheSpecSpells_StoreOneLessThanTheyMean()
    {
        SpellStores spells = SpellStores.Load(ClientData.DbcDirectory);

        List<int> selectPoints = [];
        List<int> countPoints = [];

        foreach (SpellEntry spell in spells.Spells.Entries)
        {
            foreach (SpellEffectEntry effect in spell.Effects)
            {
                if (effect.Effect == SpellEffectId.TalentSpecSelect)
                {
                    selectPoints.Add(effect.BasePoints);
                }
                else if (effect.Effect == SpellEffectId.TalentSpecCount)
                {
                    countPoints.Add(effect.BasePoints);
                }
            }
        }

        Assert.NotEmpty(selectPoints);
        Assert.NotEmpty(countPoints);

        // Select: spec 1 and spec 2, stored as 0 and 1 — already the index this code wants.
        Assert.All(selectPoints, points => Assert.InRange(points, 0, 1));

        // Count: "you now have N specs", stored as N-1.
        Assert.All(countPoints, points => Assert.InRange(points, 0, 1));
    }

    /// <summary>
    /// Switching spec takes the old build's spells off.
    /// </summary>
    /// <remarks>
    /// Leaving them makes dual spec a way to have both builds active at once, for the price of
    /// one — which is the single thing dual spec must not be.
    /// </remarks>
    [RequiresClientDataFact]
    public void SwitchingSpec_TakesTheOldBuildOff()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores, minRanks: 2);

        player.Talents.SpecCount = 2;
        player.Talents.FreePoints = 51;
        player.Talents.Learn(talent.Id, 1, stores.Talents, stores.TalentTabs, out _);

        IReadOnlyList<uint>? removed = player.Talents.Activate(1, stores.Talents);

        Assert.NotNull(removed);
        Assert.Equal(2, removed.Count);
        Assert.Empty(player.Talents.Active);
        Assert.Equal(1, player.Talents.ActiveSpec);
    }

    /// <summary>Switching back restores the first spec's points and talents.</summary>
    [RequiresClientDataFact]
    public void SwitchingBack_RestoresTheFirstBuild()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, TalentEntry talent) = Warrior(stores, minRanks: 2);

        player.Talents.SpecCount = 2;
        player.Talents.FreePoints = 51;
        player.Talents.Learn(talent.Id, 1, stores.Talents, stores.TalentTabs, out _);

        player.Talents.Activate(1, stores.Talents);
        player.Talents.Activate(0, stores.Talents);

        Assert.Single(player.Talents.Active);
        Assert.Equal(49u, player.Talents.FreePoints);
    }

    /// <summary>
    /// A character without dual spec cannot switch.
    /// </summary>
    /// <remarks>
    /// The client draws only one tab, so nothing legitimate asks — and a switch to an empty second
    /// spec silently unlearns everything.
    /// </remarks>
    [RequiresClientDataFact]
    public void WithoutDualSpec_SwitchingIsRefused()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);
        (Player player, _) = Warrior(stores);

        Assert.Null(player.Talents.Activate(1, stores.Talents));
        Assert.Equal(0, player.Talents.ActiveSpec);
    }

    // ------------------------------------------------------------------ helpers

    private static void SpendInRowZero(Player player, DbcStores stores, uint tabId, uint points)
    {
        uint spent = 0;

        foreach (TalentEntry entry in stores.Talents.Entries)
        {
            if (spent >= points)
            {
                break;
            }

            if (entry.TabId != tabId || entry.Row != 0 || entry.DependsOnTalent != 0)
            {
                continue;
            }

            uint want = Math.Min(points - spent, (uint)entry.RankCount);

            if (player.Talents.Learn(
                entry.Id, (byte)(want - 1), stores.Talents, stores.TalentTabs, out _)
                == TalentResult.Ok)
            {
                spent += want;
            }
        }

        Assert.Equal(points, spent);
    }

    /// <summary>A warrior, and a row-0 talent from one of their trees.</summary>
    private static (Player Player, TalentEntry Talent) Warrior(DbcStores stores, int minRanks = 1)
    {
        uint warriorMask = PlayerTalents.ClassMaskOf(1);

        TalentEntry talent = stores.Talents.Entries.First(
            entry => entry.Row == 0
                && entry.DependsOnTalent == 0
                && entry.RankCount >= minRanks
                && stores.TalentTabs.TryGet(entry.TabId, out TalentTabEntry? tab)
                && tab is not null
                && (tab.ClassMask & warriorMask) != 0);

        return (Character(), talent);
    }

    /// <summary>A character of whichever class owns a tree.</summary>
    private static Player PlayerOfClassFor(DbcStores stores, uint tabId)
    {
        Assert.True(stores.TalentTabs.TryGet(tabId, out TalentTabEntry? tab));
        Assert.NotNull(tab);

        for (byte classId = 1; classId <= 11; classId++)
        {
            if ((tab.ClassMask & PlayerTalents.ClassMaskOf(classId)) != 0)
            {
                return Character(classId: classId);
            }
        }

        return Character();
    }

    private static Player Character(byte level = 60, byte classId = 1) =>
        InventoryFixture.Player(level: level, characterClass: classId, proficiencies: false);
}
