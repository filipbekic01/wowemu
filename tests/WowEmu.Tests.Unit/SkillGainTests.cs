using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Raising a skill by using it.
/// </summary>
/// <remarks>
/// Without this the skill system is a set of numbers handed out at creation and never touched
/// again — weapon skill sits at 1 forever and a profession can never leave the value its trainer
/// granted. Only combat is wired up, because combat is the only system that exists to raise
/// anything.
/// </remarks>
public sealed class SkillGainTests
{
    // ------------------------------------------------------------------ the raise itself

    /// <summary>A skill in progress goes up by a point.</summary>
    [Fact]
    public void ASkillInProgress_GoesUp()
    {
        Player player = Fighter();
        player.Skills.Set(SkillType.Swords, 0, 40, 100);

        Assert.True(SkillGain.Raise(player, SkillType.Swords));
        Assert.Equal(41, player.Skills.PureValue(SkillType.Swords));
    }

    /// <summary>A skill at its ceiling does not.</summary>
    [Fact]
    public void ASkillAtItsCeiling_DoesNot()
    {
        Player player = Fighter();
        player.Skills.Set(SkillType.Swords, 0, 100, 100);

        Assert.False(SkillGain.Raise(player, SkillType.Swords));
        Assert.Equal(100, player.Skills.PureValue(SkillType.Swords));
    }

    /// <summary>A step that would overshoot lands on the ceiling.</summary>
    [Fact]
    public void AStepThatOvershoots_LandsOnTheCeiling()
    {
        Player player = Fighter();
        player.Skills.Set(SkillType.Swords, 0, 99, 100);

        Assert.True(SkillGain.Raise(player, SkillType.Swords, step: 10));
        Assert.Equal(100, player.Skills.PureValue(SkillType.Swords));
    }

    /// <summary>
    /// A skill cannot be left sitting at zero in the first place.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>UpdateSkill</c> guards against a value of zero separately from an unknown
    /// skill, and that guard is ported — but it is unreachable through our API, because
    /// <see cref="PlayerSkills.Set"/> treats a value of zero as "forget this skill", including on
    /// the restore path. This pins the reason the guard looks dead: it is upstream's invariant, not
    /// a live branch, and the thing actually keeping the invariant is the removal semantics here.
    /// </remarks>
    [Fact]
    public void ASkillCannotBeLeftAtZero()
    {
        Player player = Fighter();
        player.Skills.Set(SkillType.Swords, 0, 40, 100);

        player.Skills.Restore([((ushort)SkillType.Swords, (ushort)0, (ushort)100, (ushort)0)]);

        Assert.False(player.Skills.Has(SkillType.Swords));
        Assert.Equal(0, player.Skills.Count);
    }

    /// <summary>A skill the character does not have cannot go up.</summary>
    [Fact]
    public void AnUnknownSkill_CannotGoUp() =>
        Assert.False(SkillGain.Raise(Fighter(), SkillType.Swords));

    // ------------------------------------------------------------------ the chance

    /// <summary>At the cap for the level there is nothing left to learn.</summary>
    [Fact]
    public void AtTheCap_ThereIsNoChance()
    {
        Player player = Fighter(level: 10);
        player.Skills.Set(SkillType.Unarmed, 0, 50, 50);

        Assert.Equal(0f, SkillGain.CombatChance(player, Mob(level: 10), defending: false));
    }

    /// <summary>
    /// The chance never drops below one percent.
    /// </summary>
    /// <remarks>
    /// Grinding something grey would otherwise give a chance of zero, and a skill one point short of
    /// its cap could never be finished off at all.
    /// </remarks>
    [Fact]
    public void TheChance_IsFlooredAtOnePercent()
    {
        Player player = Fighter(level: 60);
        player.Skills.Set(SkillType.Defense, 0, 299, 300);

        // A level-1 rat, which is as grey as it gets.
        float chance = SkillGain.CombatChance(player, Mob(level: 1), defending: true);

        Assert.Equal(1f, chance);
    }

    /// <summary>
    /// A much higher opponent is worth no more than five levels above you.
    /// </summary>
    /// <remarks>
    /// Without the cap, one exchange with something enormous is worth a level's worth of grinding —
    /// and standing next to a raid boss becomes the fastest way to cap a weapon skill.
    /// </remarks>
    [Fact]
    public void AMuchHigherOpponent_IsCappedAtFiveLevelsAbove()
    {
        Player player = Fighter(level: 20);
        player.Skills.Set(SkillType.Defense, 0, 1, 100);

        float atFiveAbove = SkillGain.CombatChance(player, Mob(level: 25), defending: true);
        float enormous = SkillGain.CombatChance(player, Mob(level: 80), defending: true);

        Assert.Equal(atFiveAbove, enormous);
    }

    /// <summary>
    /// Intellect helps you learn to swing, and does nothing for learning to take a hit.
    /// </summary>
    /// <remarks>
    /// An old rule that looks like a bug until you find it in the source — the bonus is applied only
    /// on the attacking side.
    /// </remarks>
    [Fact]
    public void Intellect_HelpsTheAttackerOnly()
    {
        Player dim = Fighter(level: 20);
        dim.Skills.Set(SkillType.Unarmed, 0, 1, 100);
        dim.Skills.Set(SkillType.Defense, 0, 1, 100);
        dim.SetStat(StatIntellect, 10);

        Player bright = Fighter(level: 20);
        bright.Skills.Set(SkillType.Unarmed, 0, 1, 100);
        bright.Skills.Set(SkillType.Defense, 0, 1, 100);
        bright.SetStat(StatIntellect, 100);

        Creature mob = Mob(level: 20);

        Assert.True(
            SkillGain.CombatChance(bright, mob, defending: false)
            > SkillGain.CombatChance(dim, mob, defending: false));

        Assert.Equal(
            SkillGain.CombatChance(dim, mob, defending: true),
            SkillGain.CombatChance(bright, mob, defending: true));
    }

    /// <summary>Being further behind the cap is worth more.</summary>
    [Fact]
    public void BeingFurtherBehind_IsWorthMore()
    {
        Player behind = Fighter(level: 20);
        behind.Skills.Set(SkillType.Defense, 0, 1, 100);

        Player nearlyThere = Fighter(level: 20);
        nearlyThere.Skills.Set(SkillType.Defense, 0, 99, 100);

        Creature mob = Mob(level: 20);

        Assert.True(
            SkillGain.CombatChance(behind, mob, defending: true)
            > SkillGain.CombatChance(nearlyThere, mob, defending: true));
    }

    // ------------------------------------------------------------------ which skill goes up

    /// <summary>The weapon in hand decides.</summary>
    [Fact]
    public void TheWeaponInHand_DecidesWhatGoesUp()
    {
        Player player = Fighter(level: 20);
        player.Skills.Set(SkillType.Swords, 0, 1, 100);

        Wield(player, SwordSubClass);

        Assert.Equal(
            SkillType.Swords,
            SkillGain.RollCombat(player, Mob(level: 20), defending: false, AlwaysHits));

        Assert.Equal(2, player.Skills.PureValue(SkillType.Swords));
    }

    /// <summary>
    /// Empty-handed raises Unarmed and Fist Weapons together.
    /// </summary>
    /// <remarks>
    /// They are meant to track each other. Letting them drift means picking up a fist weapon after
    /// a hundred bare-handed levels suddenly swings at a worse skill than your own hands.
    /// </remarks>
    [Fact]
    public void EmptyHanded_RaisesUnarmedAndFistTogether()
    {
        Player player = Fighter(level: 20);
        player.Skills.Set(SkillType.Unarmed, 0, 40, 100);
        player.Skills.Set(SkillType.FistWeapons, 0, 40, 100);

        Assert.Equal(
            SkillType.Unarmed,
            SkillGain.RollCombat(player, Mob(level: 20), defending: false, AlwaysHits));

        Assert.Equal(41, player.Skills.PureValue(SkillType.Unarmed));
        Assert.Equal(41, player.Skills.PureValue(SkillType.FistWeapons));
    }

    /// <summary>And a fist weapon raises Unarmed as well as itself — the same rule the other way.</summary>
    [Fact]
    public void AFistWeapon_RaisesUnarmedToo()
    {
        Player player = Fighter(level: 20);
        player.Skills.Set(SkillType.Unarmed, 0, 40, 100);
        player.Skills.Set(SkillType.FistWeapons, 0, 40, 100);

        Wield(player, FistSubClass);

        Assert.Equal(
            SkillType.FistWeapons,
            SkillGain.RollCombat(player, Mob(level: 20), defending: false, AlwaysHits));

        Assert.Equal(41, player.Skills.PureValue(SkillType.FistWeapons));
        Assert.Equal(41, player.Skills.PureValue(SkillType.Unarmed));
    }

    /// <summary>
    /// Beating something to death with a fishing pole teaches nothing.
    /// </summary>
    /// <remarks>
    /// It is not fishing, and it is not swordsmanship either. Without the exception the pole falls
    /// through to <c>ForItem</c>, which maps its subclass to Fishing — so a fistfight with a carp
    /// would raise your fishing skill.
    /// </remarks>
    [Fact]
    public void AFishingPole_TeachesNothing()
    {
        Player player = Fighter(level: 20);
        player.Skills.Set(SkillType.Fishing, 0, 40, 100);

        Wield(player, FishingPoleSubClass);

        Assert.Equal(0u, SkillGain.RollCombat(player, Mob(level: 20), defending: false, AlwaysHits));
        Assert.Equal(40, player.Skills.PureValue(SkillType.Fishing));
    }

    /// <summary>Defending raises defence, whatever is in hand.</summary>
    [Fact]
    public void Defending_RaisesDefence()
    {
        Player player = Fighter(level: 20);
        player.Skills.Set(SkillType.Defense, 0, 40, 100);

        Wield(player, SwordSubClass);

        Assert.Equal(
            SkillType.Defense,
            SkillGain.RollCombat(player, Mob(level: 20), defending: true, AlwaysHits));

        Assert.Equal(41, player.Skills.PureValue(SkillType.Defense));
    }

    /// <summary>A failed roll changes nothing.</summary>
    [Fact]
    public void AFailedRoll_ChangesNothing()
    {
        Player player = Fighter(level: 20);
        player.Skills.Set(SkillType.Defense, 0, 40, 100);

        Assert.Equal(0u, SkillGain.RollCombat(player, Mob(level: 20), defending: true, NeverHits));
        Assert.Equal(40, player.Skills.PureValue(SkillType.Defense));
    }

    // ------------------------------------------------------------------ which outcomes teach

    /// <summary>
    /// A hit, a miss, a dodge and a parry all teach; a crit and a glancing blow do not.
    /// </summary>
    /// <remarks>
    /// Upstream's proc mask. A crit is not a lesson — you already connected, and there is nothing in
    /// the outcome to learn from.
    /// </remarks>
    [Theory]
    [InlineData(MeleeHitOutcome.Normal, true)]
    [InlineData(MeleeHitOutcome.Miss, true)]
    [InlineData(MeleeHitOutcome.Dodge, true)]
    [InlineData(MeleeHitOutcome.Parry, true)]
    [InlineData(MeleeHitOutcome.Crit, false)]
    [InlineData(MeleeHitOutcome.Glancing, false)]
    [InlineData(MeleeHitOutcome.Crushing, false)]
    [InlineData(MeleeHitOutcome.Evade, false)]
    public void OutcomesThatTeach(MeleeHitOutcome outcome, bool teaches) =>
        Assert.Equal(teaches, SkillGain.Teaches(outcome, defending: false));

    /// <summary>
    /// A block teaches the defender and nobody else.
    /// </summary>
    /// <remarks>
    /// Blocking is what defence is for, and it tells the attacker nothing about swinging.
    /// </remarks>
    [Fact]
    public void ABlock_TeachesTheDefenderOnly()
    {
        Assert.True(SkillGain.Teaches(MeleeHitOutcome.Block, defending: true));
        Assert.False(SkillGain.Teaches(MeleeHitOutcome.Block, defending: false));
    }

    // ------------------------------------------------------------------ fixtures

    private const int StatIntellect = 3;
    private const byte SwordSubClass = 7;
    private const byte FistSubClass = 13;
    private const byte FishingPoleSubClass = 20;

    /// <summary>A roll that always succeeds, so the chance is not what is under test.</summary>
    private static uint AlwaysHits(uint min, uint max) => min;

    /// <summary>And one that never does.</summary>
    private static uint NeverHits(uint min, uint max) => max;

    private static Player Fighter(byte level = 20) =>
        InventoryFixture.Player(level: level, proficiencies: false);

    private static Creature Mob(byte level)
    {
        Creature creature = CreatureFixture.Build();
        creature.Level = level;

        return creature;
    }

    private static void Wield(Player player, byte subClass) =>
        InventoryFixture.Place(
            player,
            ItemFixture.Build(
                entry: 1,
                itemClass: ItemClass.Weapon,
                subClass: subClass,
                inventoryType: InventoryType.WeaponMainHand),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));
}
