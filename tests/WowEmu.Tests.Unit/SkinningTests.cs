using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Skinning and pickpocketing on a live map.
/// </summary>
/// <remarks>
/// The rules are unit-tested elsewhere; this is about the two things only a map can show — when the
/// corpse becomes skinnable, and that a picked pocket does not become a second corpse loot.
/// </remarks>
public sealed class SkinningTests
{
    private const uint CorpseLootId = 800;
    private const uint SkinLootId = 900;
    private const uint PocketLootId = 950;
    private const uint MeatEntry = 6001;
    private const uint HideEntry = 6002;
    private const uint CoinEntry = 6003;

    /// <summary>
    /// A corpse with loot still on it is not skinnable.
    /// </summary>
    /// <remarks>
    /// <b>Skinning is a second pass.</b> Flagging at death lets a skinner take the hide out from
    /// under whoever killed it: the corpse sparkles for both and only one of them owns the loot.
    /// </remarks>
    [Fact]
    public void AFullCorpse_IsNotYetSkinnable()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link _) = World();

        Kill(map, player, victim);

        Assert.NotNull(victim.Loot);
        Assert.NotEmpty(victim.Loot.Items);
        Assert.Equal(0u, victim.UnitFlags & (uint)UnitFlags.Skinnable);
    }

    /// <summary>And becomes skinnable the moment it is emptied.</summary>
    [Fact]
    public void AnEmptiedCorpse_BecomesSkinnable()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link _) = World();

        Kill(map, player, victim);
        EmptyTheCorpse(map, player, victim);

        Assert.NotEqual(0u, victim.UnitFlags & (uint)UnitFlags.Skinnable);
    }

    /// <summary>
    /// Skinning yields the skinning table, not the corpse one.
    /// </summary>
    /// <remarks>
    /// The two ids are the same number for many creatures and mean different things — reading the
    /// corpse table gives a second helping of whatever it already dropped.
    /// </remarks>
    [Fact]
    public void Skinning_YieldsTheSkinningTable()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link _) = World();

        Kill(map, player, victim);
        EmptyTheCorpse(map, player, victim);

        Assert.True(map.Skin(player, victim));
        Assert.NotNull(victim.Loot);
        Assert.Equal([HideEntry], victim.Loot.Items.Select(item => item.ItemId));
    }

    /// <summary>
    /// A corpse cannot be skinned twice.
    /// </summary>
    /// <remarks>
    /// The flag comes off before the loot goes on. Leaving it would let a skinner roll a fresh hide
    /// off the same body indefinitely, which is a duplication bug rather than a cosmetic one.
    /// </remarks>
    [Fact]
    public void ACorpse_CannotBeSkinnedTwice()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link _) = World();

        Kill(map, player, victim);
        EmptyTheCorpse(map, player, victim);

        Assert.True(map.Skin(player, victim));
        Assert.False(map.Skin(player, victim));
    }

    /// <summary>
    /// A creature with no skin loot never becomes skinnable.
    /// </summary>
    /// <remarks>
    /// Checked against the table rather than the id: a creature can carry an id whose table has no
    /// rows, and flagging on the id makes the corpse sparkle and then hands over nothing.
    /// </remarks>
    [Fact]
    public void ACreatureWithNoHide_NeverSparkles()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link _) = World(skinLootId: 0);

        Kill(map, player, victim);
        EmptyTheCorpse(map, player, victim);

        Assert.Equal(0u, victim.UnitFlags & (uint)UnitFlags.Skinnable);
    }

    // ------------------------------------------------------------------ pickpocketing

    /// <summary>
    /// A pocket can be picked while the creature is alive.
    /// </summary>
    /// <remarks>
    /// The one loot path that wants a living target, which is why it cannot go through the ordinary
    /// loot open — that refuses anything still breathing.
    /// </remarks>
    [Fact]
    public void APocket_IsPickedWhileAlive()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link _) = World();

        Assert.True(victim.IsAlive);
        Assert.True(map.Pickpocket(player, victim, nowSeconds: 1000));
        Assert.NotNull(victim.Loot);
        Assert.Equal([CoinEntry], victim.Loot.Items.Select(item => item.ItemId));
    }

    /// <summary>
    /// A pocket already picked stays empty until its cooldown.
    /// </summary>
    /// <remarks>
    /// Per creature and not per player: a picked pocket is empty for everyone, so a second rogue
    /// gets nothing either.
    /// </remarks>
    [Fact]
    public void APickedPocket_StaysEmpty()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link _) = World();

        Assert.True(map.Pickpocket(player, victim, nowSeconds: 1000));
        Assert.False(map.Pickpocket(player, victim, nowSeconds: 1001));

        // A minute, plus the corpse delay (60s for a common rank) and the respawn delay (120s in
        // the fixture) — so 240 seconds, not the flat minute the name suggests.
        Assert.False(map.Pickpocket(player, victim, nowSeconds: 1000 + 239));
        Assert.True(map.Pickpocket(player, victim, nowSeconds: 1000 + 240));
    }

    /// <summary>A dead creature has no pockets to pick.</summary>
    [Fact]
    public void ADeadCreature_HasNoPockets()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link _) = World();

        Kill(map, player, victim);

        Assert.False(map.Pickpocket(player, victim, nowSeconds: 1000));
    }

    // ------------------------------------------------------------------ helpers

    private static void Kill(Map map, Player player, Creature victim)
    {
        player.AttackStop();

        // Through the threat list: it is what decides who owns the corpse, and a corpse with no
        // owner refuses the loot open this whole file depends on.
        victim.Threat.AddThreat(player, 100f);
        victim.Health = 0;

        map.Kill(victim);
    }

    /// <summary>Takes everything off the corpse, which is what makes it skinnable.</summary>
    private static void EmptyTheCorpse(Map map, Player player, Creature victim)
    {
        map.OpenLoot(player, victim.Guid);

        // Backwards: taking slot 0 first renumbers the rest under the loop.
        for (int slot = (victim.Loot?.Items.Count ?? 0) - 1; slot >= 0; slot--)
        {
            map.TakeLoot(player, (byte)slot);
        }
    }

    private static (Map, Player, Creature, MapCombatFixture.Link) World(uint skinLootId = SkinLootId)
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) =
            MapCombatFixture.Engaged(
                items: LootFixture.Items(
                    ItemFixture.Build(entry: MeatEntry, name: "Meat"),
                    ItemFixture.Build(entry: HideEntry, name: "Hide"),
                    ItemFixture.Build(entry: CoinEntry, name: "Coin")),
                creatureLoot: LootFixture.Store(
                    "creature_loot_template",
                    CorpseLootId,
                    LootFixture.Template(LootFixture.Row(itemId: MeatEntry, chance: 100f))),
                lootReferences: LootFixture.References(1, LootFixture.Template()),
                lootId: CorpseLootId,
                skinningLoot: LootFixture.Store(
                    "skinning_loot_template",
                    SkinLootId,
                    LootFixture.Template(LootFixture.Row(itemId: HideEntry, chance: 100f))),
                pickpocketLoot: LootFixture.Store(
                    "pickpocketing_loot_template",
                    PocketLootId,
                    LootFixture.Template(LootFixture.Row(itemId: CoinEntry, chance: 100f))),
                skinLootId: skinLootId,
                pickpocketLootId: PocketLootId);

        return (map, player, victim, link);
    }
}
