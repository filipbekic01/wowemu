using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using WowEmu.Protocol;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// <c>CalcValue</c>: how big an effect is for a given caster.
/// </summary>
public sealed class SpellEffectValueTests
{
    private static SpellEntry Spell(uint baseLevel = 0, uint spellLevel = 0, uint maxLevel = 0) =>
        new(1, "test", "", new uint[SpellEntry.AttributeWords],
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0f, 0,
            BaseLevel: baseLevel, SpellLevel: spellLevel, MaxLevel: maxLevel,
            0, 0, 0, 0, 0, 0, 0, new SpellEffectEntry[SpellConstants.MaxEffects]);

    private static SpellEffectEntry Effect(int basePoints, int dieSides, float perLevel = 0f) =>
        new(SpellEffectId.SchoolDamage, dieSides, perLevel, basePoints, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Always rolls the low end, so the arithmetic around it is what is under test.</summary>
    private static int Lowest(int min, int max) => min;

    private static int Highest(int min, int max) => max;

    /// <summary>
    /// The roll is over <c>[1, sides]</c>, which is why the stored base is one below the minimum.
    /// </summary>
    /// <remarks>
    /// Rolling from zero instead would make every spell in the game hit for one less at the bottom
    /// of its range — uniform, plausible, and invisible without a reference.
    /// </remarks>
    [Fact]
    public void TheRoll_IsOverOneToSides()
    {
        // Fireball rank 1: base 13, 9 sides, so 14-22.
        SpellEffectEntry fireball = Effect(basePoints: 13, dieSides: 9);

        Assert.Equal(14, SpellEffects.CalculateValue(Spell(), fireball, 1, Lowest));
        Assert.Equal(22, SpellEffects.CalculateValue(Spell(), fireball, 1, Highest));
    }

    /// <summary>
    /// A single side adds exactly one and draws no random number.
    /// </summary>
    /// <remarks>
    /// Upstream has an explicit <c>case 1</c> before the general roll. Folding it into
    /// <c>irand(1, 1)</c> gives the same answer but consumes a draw, and the two random streams
    /// diverge from that point on.
    /// </remarks>
    [Fact]
    public void ASingleSide_AddsOneWithoutDrawing()
    {
        int draws = 0;

        int value = SpellEffects.CalculateValue(
            Spell(), Effect(basePoints: 9, dieSides: 1), 1, (_, _) => { draws++; return 0; });

        Assert.Equal(10, value);
        Assert.Equal(0, draws);
    }

    /// <summary>No sides at all means the base stands alone.</summary>
    [Fact]
    public void NoSides_LeavesTheBaseAlone()
    {
        int draws = 0;

        int value = SpellEffects.CalculateValue(
            Spell(), Effect(basePoints: 50, dieSides: 0), 1, (_, _) => { draws++; return 0; });

        Assert.Equal(50, value);
        Assert.Equal(0, draws);
    }

    /// <summary>
    /// Level scaling counts from the spell's own level, not from zero.
    /// </summary>
    /// <remarks>
    /// The level is reduced by <c>max(BaseLevel, SpellLevel)</c> before being multiplied. Using the
    /// caster's raw level makes a rank 1 spell scale as though it had been learned at level 0, and
    /// a level 80 caster's Fireball rank 1 hits for hundreds.
    /// </remarks>
    [Fact]
    public void LevelScaling_CountsFromTheSpellsOwnLevel()
    {
        SpellEntry spell = Spell(baseLevel: 10, spellLevel: 10);
        SpellEffectEntry effect = Effect(basePoints: 100, dieSides: 0, perLevel: 2f);

        // At the spell's own level there is no bonus at all.
        Assert.Equal(100, SpellEffects.CalculateValue(spell, effect, 10, Lowest));

        // Five levels above: 5 × 2.
        Assert.Equal(110, SpellEffects.CalculateValue(spell, effect, 15, Lowest));
    }

    /// <summary>Below the spell's base level the caster is treated as being at it.</summary>
    [Fact]
    public void BelowTheBaseLevel_TheBonusDoesNotGoNegative()
    {
        SpellEntry spell = Spell(baseLevel: 20, spellLevel: 20);
        SpellEffectEntry effect = Effect(basePoints: 100, dieSides: 0, perLevel: 2f);

        Assert.Equal(100, SpellEffects.CalculateValue(spell, effect, 1, Lowest));
    }

    /// <summary>Above the spell's maximum level the bonus stops growing.</summary>
    [Fact]
    public void AboveTheMaxLevel_TheBonusStops()
    {
        SpellEntry spell = Spell(baseLevel: 10, spellLevel: 10, maxLevel: 20);
        SpellEffectEntry effect = Effect(basePoints: 100, dieSides: 0, perLevel: 2f);

        // Capped at 20: (20 - 10) × 2.
        Assert.Equal(120, SpellEffects.CalculateValue(spell, effect, 20, Lowest));
        Assert.Equal(120, SpellEffects.CalculateValue(spell, effect, 80, Lowest));
    }

    /// <summary>A maximum of zero means no cap, not a cap at zero.</summary>
    [Fact]
    public void AZeroMaxLevel_MeansNoCap()
    {
        SpellEntry spell = Spell(baseLevel: 1, spellLevel: 1, maxLevel: 0);
        SpellEffectEntry effect = Effect(basePoints: 0, dieSides: 0, perLevel: 1f);

        Assert.Equal(79, SpellEffects.CalculateValue(spell, effect, 80, Lowest));
    }

    /// <summary>An effect with no per-level growth ignores the caster's level entirely.</summary>
    [Fact]
    public void WithNoPerLevelGrowth_LevelIsIgnored()
    {
        SpellEntry spell = Spell(baseLevel: 10, spellLevel: 10);
        SpellEffectEntry effect = Effect(basePoints: 100, dieSides: 0);

        Assert.Equal(100, SpellEffects.CalculateValue(spell, effect, 1, Lowest));
        Assert.Equal(100, SpellEffects.CalculateValue(spell, effect, 80, Lowest));
    }
}

/// <summary>Applying a spell's effects to a target.</summary>
public sealed class SpellApplyTests
{
    private static SpellEntry WithEffects(uint schoolMask, params SpellEffectEntry[] effects)
    {
        SpellEffectEntry[] slots = new SpellEffectEntry[SpellConstants.MaxEffects];

        for (int i = 0; i < effects.Length && i < slots.Length; i++)
        {
            slots[i] = effects[i];
        }

        return new SpellEntry(
            1, "test", "", new uint[SpellEntry.AttributeWords],
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0f, 0, 0, 0, 0,
            SchoolMask: schoolMask, DmgClass: 0, PreventionType: 0, SpellFamilyName: 0,
            MaxAffectedTargets: 0, SpellIconId: 0, SpellVisual: 0, Effects: slots);
    }

    // One die side by default, which is how a flat-value effect is actually stored: the roll adds
    // exactly 1, so the effect is worth `basePoints + 1`. Zero sides would add nothing and make
    // every expectation here one lower than the value a real spell of that shape carries.
    private static SpellEffectEntry Damage(int basePoints, int dieSides = 1) =>
        new(SpellEffectId.SchoolDamage, dieSides, 0f, basePoints, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static SpellEffectEntry Heal(int basePoints) =>
        new(SpellEffectId.Heal, 1, 0f, basePoints, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static SpellEffectEntry WeaponBonus(int basePoints) =>
        new(SpellEffectId.WeaponDamage, 1, 0f, basePoints, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Always the low end, so armour and summing are what is being measured.</summary>
    private static uint Lowest(uint min, uint max) => min;

    [Fact]
    public void ADamageSpell_TakesHealth()
    {
        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        target.Armor = 0;

        // Fire, so armour does not apply.
        SpellHit hit = SpellEffects.Apply(caster, target, WithEffects(4, Damage(99)), Lowest);

        Assert.Equal(100u, hit.Damage);
        Assert.Equal(0u, hit.Healing);
        Assert.Equal(4u, hit.SchoolMask);
        Assert.False(hit.IsPhysical);
    }

    /// <summary>
    /// Only physical spell damage goes through armour.
    /// </summary>
    /// <remarks>
    /// Mitigating a frostbolt with armour would give a plate wearer half damage from every school,
    /// which is a very different game.
    /// </remarks>
    [Fact]
    public void OnlyPhysicalDamage_GoesThroughArmour()
    {
        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        target.Armor = 5000;

        SpellHit magic = SpellEffects.Apply(caster, target, WithEffects(4, Damage(99)), Lowest);
        SpellHit physical = SpellEffects.Apply(caster, target, WithEffects(1, Damage(99)), Lowest);

        Assert.Equal(100u, magic.Damage);
        Assert.True(physical.Damage < 100u, "armour did not reduce physical spell damage");
        Assert.True(physical.IsPhysical);
        Assert.False(magic.IsPhysical);
    }

    [Fact]
    public void AHeal_GivesHealthAndDealsNoDamage()
    {
        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        SpellHit hit = SpellEffects.Apply(caster, target, WithEffects(2, Heal(49)), Lowest);

        Assert.Equal(50u, hit.Healing);
        Assert.Equal(0u, hit.Damage);
    }

    /// <summary>
    /// A weapon-damage effect adds a swing, not just its own value.
    /// </summary>
    /// <remarks>
    /// The value is a flat bonus on top of the weapon's own roll. Treating it as damage in its own
    /// right makes Heroic Strike hit for 11 instead of for a swing plus 11.
    /// </remarks>
    [Fact]
    public void AWeaponDamageEffect_AddsASwingOnTopOfItsBonus()
    {
        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        target.Armor = 0;

        // The fixture creature swings for 6-8; the effect's value is 11.
        SpellHit hit = SpellEffects.Apply(caster, target, WithEffects(1, WeaponBonus(10)), Lowest);

        Assert.Equal((uint)caster.MinDamage + 11u, hit.Damage);
    }

    /// <summary>All three weapon-damage forms behave the same way here.</summary>
    [Theory]
    [InlineData(SpellEffectId.WeaponDamage)]
    [InlineData(SpellEffectId.WeaponDamageNoSchool)]
    [InlineData(SpellEffectId.NormalizedWeaponDamage)]
    public void EveryWeaponDamageForm_IsRecognised(uint effectId)
    {
        Assert.True(SpellEffectId.IsWeaponDamage(effectId));

        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        target.Armor = 0;

        SpellEntry spell = WithEffects(1, new SpellEffectEntry(effectId, 1, 0f, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        Assert.Equal((uint)caster.MinDamage + 11u, SpellEffects.Apply(caster, target, spell, Lowest).Damage);
    }

    /// <summary>Several damage effects on one spell are summed.</summary>
    [Fact]
    public void SeveralDamageEffects_AreSummed()
    {
        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        target.Armor = 0;

        SpellHit hit = SpellEffects.Apply(
            caster, target, WithEffects(4, Damage(9), Damage(19), Damage(29)), Lowest);

        Assert.Equal(10u + 20u + 30u, hit.Damage);
    }

    /// <summary>
    /// Armour is applied once at the end, not per effect.
    /// </summary>
    /// <remarks>
    /// Armour mitigation rounds <i>up</i>, so mitigating each effect separately gains a point per
    /// effect — a three-effect spell would land for two more than it should.
    /// </remarks>
    [Fact]
    public void Armour_IsAppliedOnceNotPerEffect()
    {
        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        target.Armor = 3000;

        SpellHit combined = SpellEffects.Apply(
            caster, target, WithEffects(1, Damage(9), Damage(9), Damage(9)), Lowest);

        uint mitigatedOnce = ArmorMitigation.Reduce(30, target.Armor, caster.Level);
        uint mitigatedThrice = 3 * ArmorMitigation.Reduce(10, target.Armor, caster.Level);

        Assert.Equal(mitigatedOnce, combined.Damage);
        Assert.True(mitigatedThrice > mitigatedOnce, "the two orders should differ, or this proves nothing");
    }

    /// <summary>An unused effect slot contributes nothing.</summary>
    [Fact]
    public void AnUnusedSlot_ContributesNothing()
    {
        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        target.Armor = 0;

        SpellHit hit = SpellEffects.Apply(caster, target, WithEffects(4, Damage(9)), Lowest);

        Assert.Equal(10u, hit.Damage);
    }

    /// <summary>A spell with nothing this server handles does nothing at all.</summary>
    [Fact]
    public void AnUnhandledEffect_DoesNothing()
    {
        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        // Effect 6 is APPLY_AURA, which is not handled yet.
        SpellEntry spell = WithEffects(32, new SpellEffectEntry(6, 0, 0f, 100, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        SpellHit hit = SpellEffects.Apply(caster, target, spell, Lowest);

        Assert.False(hit.IsAnything);
    }

    // ------------------------------------------------------------------ against the real data

    /// <summary>
    /// Fireball rank 1 lands for what its tooltip says.
    /// </summary>
    /// <remarks>
    /// The end-to-end check on the whole chain: the DBC layout, <c>CalcValue</c>'s roll, and the
    /// school deciding whether armour applies. All three have to be right for the number to come
    /// out at 14-22.
    /// </remarks>
    [RequiresClientDataFact]
    public void RealFireball_LandsForItsTooltipRange()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        Creature caster = CreatureFixture.Build();
        Creature target = CreatureFixture.Build();

        caster.Level = 1;
        target.Armor = 5000;   // plate, which fire ignores

        Assert.Equal(14u, SpellEffects.Apply(caster, target, fireball, (min, _) => min).Damage);
        Assert.Equal(22u, SpellEffects.Apply(caster, target, fireball, (_, max) => max).Damage);
    }
}

/// <summary>The combat-log packet for spell damage.</summary>
public sealed class SpellDamageLogTests
{
    private static readonly ObjectGuid Caster = ObjectGuid.Create(HighGuid.Player, 7);
    private static readonly ObjectGuid Target = ObjectGuid.Create(HighGuid.Unit, 299, 42);

    private static byte[] Write(in SpellDamageLog log)
    {
        PacketWriter writer = new();
        SpellDamageLogPacket.Write(writer, log);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The target is written first, then the caster.
    /// </summary>
    /// <remarks>
    /// The opposite order to <c>SMSG_ATTACKERSTATEUPDATE</c>, which leads with the attacker. Two
    /// packets describing the same kind of event with the operands reversed, and nothing in either
    /// says so — swapping them makes the client attribute every spell to its own victim.
    /// </remarks>
    [Fact]
    public void TheTarget_ComesBeforeTheCaster()
    {
        byte[] bytes = Write(new SpellDamageLog(Target, Caster, 133, 250, 1000, 4));

        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid first));
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid second));

        Assert.Equal(Target, first);
        Assert.Equal(Caster, second);
    }

    [Fact]
    public void TheBody_ReadsBackFieldByField()
    {
        byte[] bytes = Write(new SpellDamageLog(
            Target, Caster, SpellId: 133, Damage: 250, TargetHealth: 1000,
            SchoolMask: 4, Absorbed: 10, Resisted: 20, Blocked: 30, IsPhysical: true));

        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));

        Assert.True(reader.TryReadUInt32(out uint spellId));
        Assert.Equal(133u, spellId);

        Assert.True(reader.TryReadUInt32(out uint damage));
        Assert.Equal(250u, damage);

        Assert.True(reader.TryReadUInt32(out uint overkill));
        Assert.Equal(0u, overkill);

        Assert.True(reader.TryReadUInt8(out byte school));
        Assert.Equal(4, school);

        Assert.True(reader.TryReadUInt32(out uint absorbed));
        Assert.Equal(10u, absorbed);

        Assert.True(reader.TryReadUInt32(out uint resisted));
        Assert.Equal(20u, resisted);

        Assert.True(reader.TryReadUInt8(out byte physical));
        Assert.Equal(1, physical);

        Assert.True(reader.TryReadUInt8(out byte unused));
        Assert.Equal(0, unused);

        Assert.True(reader.TryReadUInt32(out uint blocked));
        Assert.Equal(30u, blocked);

        // Hit info twice, then the debug byte.
        Assert.Equal(4 + 4 + 1, reader.Remaining);
    }

    /// <summary>Overkill is the wasted portion, and is never negative.</summary>
    [Theory]
    [InlineData(50u, 1000u, 0u)]
    [InlineData(1000u, 1000u, 0u)]
    [InlineData(1500u, 1000u, 500u)]
    public void Overkill_IsTheWastedPortion(uint damage, uint health, uint expected)
    {
        byte[] bytes = Write(new SpellDamageLog(Target, Caster, 133, damage, health, 4));

        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        reader.Skip(4 + 4);

        Assert.True(reader.TryReadUInt32(out uint overkill));
        Assert.Equal(expected, overkill);
    }
}

/// <summary>Spells landing through a real map.</summary>
public sealed class MapSpellEffectTests(ITestOutputHelper output)
{
    /// <summary>A completed cast takes the target's health and tells everyone.</summary>
    [RequiresClientDataFact]
    public void ACompletedCast_TakesHealthAndLogsIt()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        caster.AttackStop();

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        target.MaxHealth = 1000;
        target.Health = 1000;

        map.CompleteCast(caster, fireball, target, castCount: 1);

        Assert.True(target.Health < 1000u, "the spell did no damage");
        Assert.NotEmpty(link.SpellDamage);

        (ObjectGuid loggedTarget, uint spellId, SpellHit hit) = link.SpellDamage[0];

        Assert.Equal(target.Guid, loggedTarget);
        Assert.Equal(133u, spellId);
        Assert.Equal(1000u - target.Health, hit.Damage);

        output.WriteLine($"Fireball hit for {hit.Damage}, school {hit.SchoolMask}");
    }

    /// <summary>A spell generates threat, so the target fights back.</summary>
    [RequiresClientDataFact]
    public void ASpell_GeneratesThreat()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, _) = MapCombatFixture.Engaged();

        caster.AttackStop();

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        target.MaxHealth = 1000;
        target.Health = 1000;

        map.CompleteCast(caster, fireball, target, 1);

        Assert.True(target.Threat.Contains(caster));
        Assert.True(target.Threat.GetThreat(caster) > 0f);
    }

    /// <summary>A killing spell is noticed the same way a killing swing is.</summary>
    [RequiresClientDataFact]
    public void AKillingSpell_KillsTheTarget()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, _) = MapCombatFixture.Engaged();

        caster.AttackStop();

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        target.Health = 1;

        map.CompleteCast(caster, fireball, target, 1);

        Assert.Equal(0u, target.Health);
        Assert.Equal(DeathState.Corpse, target.DeathState);
    }

    /// <summary>
    /// A heal cannot take a target above its maximum.
    /// </summary>
    /// <remarks>
    /// Health is unsigned and the field reaches the client directly, so an overheal would show as a
    /// bar drawn past its own end.
    /// </remarks>
    [RequiresClientDataFact]
    public void AHeal_CannotOverfill()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, _) = MapCombatFixture.Engaged();

        caster.AttackStop();

        // Lesser Heal rank 1.
        Assert.True(stores.Spells.TryGet(2050, out SpellEntry heal));
        Assert.Contains(heal.UsedEffects, effect => effect.Effect == SpellEffectId.Heal);

        target.MaxHealth = 100;
        target.Health = 99;

        map.CompleteCast(caster, heal, target, 1);

        Assert.Equal(100u, target.Health);
    }

    /// <summary>A cast at a dead target does nothing rather than reviving the corpse's health.</summary>
    [RequiresClientDataFact]
    public void ACastAtACorpse_DoesNothing()
    {
        SpellStores stores = SpellStores.Load(ClientData.DbcDirectory);
        (Map map, Player caster, Creature target, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        caster.AttackStop();

        Assert.True(stores.Spells.TryGet(133, out SpellEntry fireball));

        target.Kill();

        map.CompleteCast(caster, fireball, target, 1);

        Assert.Equal(0u, target.Health);
        Assert.Empty(link.SpellDamage);
    }
}
