using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Protocol;

// This namespace ends in `Unit`, so a bare `Unit.PowerRage` binds to the namespace rather than to
// the class and does not compile. The IDE flags this alias as unnecessary; it is wrong — removing
// it produces CS0234 on every use below.
using GameUnit = WowEmu.Game.Unit;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Building a creature out of its template, spawn row and base stats.
/// </summary>
/// <remarks>
/// Every assertion here is about a field the client reads. A creature with the wrong display id is
/// invisible, one with no bounding radius cannot be clicked, and one with zero health is drawn as a
/// corpse — none of which raises an error anywhere. That is what makes these worth pinning.
/// </remarks>
public sealed class CreatureTests
{
    [Fact]
    public void Guid_CarriesTheEntryAndTheSpawnId()
    {
        Creature creature = Build();

        Assert.True(creature.Guid.IsCreature);
        Assert.Equal(SampleEntry, creature.Guid.Entry);
        Assert.Equal(SampleSpawnId, creature.Guid.Counter);
        Assert.Equal(SampleSpawnId, creature.SpawnId);
    }

    /// <summary>
    /// A creature carries its entry in <c>OBJECT_FIELD_ENTRY</c> and a player does not. Without it
    /// the client has no template to resolve and draws nothing.
    /// </summary>
    [Fact]
    public void Entry_IsInTheUpdateFields()
    {
        Creature creature = Build();

        Assert.Equal(SampleEntry, creature.Fields.GetUInt32(UpdateFields.OBJECT_FIELD_ENTRY));
    }

    [Fact]
    public void TypeMask_IsObjectAndUnitOnly()
    {
        Creature creature = Build();

        Assert.Equal(TypeId.Unit, creature.TypeId);
        Assert.Equal(TypeMask.Object | TypeMask.Unit, creature.Fields.GetUInt32(UpdateFields.OBJECT_FIELD_TYPE));
        Assert.Equal(0u, creature.Fields.GetUInt32(UpdateFields.OBJECT_FIELD_TYPE) & TypeMask.Player);
    }

    /// <summary>Race stays zero: a creature has a unit class but no race.</summary>
    [Fact]
    public void Bytes0_HoldsClassGenderAndPowerButNoRace()
    {
        Creature creature = Build(template: Template() with { UnitClass = UnitClassRogue });

        Assert.Equal(0, creature.Race);
        Assert.Equal(UnitClassRogue, creature.Class);
        Assert.Equal(SampleGender, creature.Gender);
        Assert.Equal(GameUnit.PowerEnergy, creature.PowerType);
    }

    [Theory]
    [InlineData(UnitClassWarrior, GameUnit.PowerRage)]
    [InlineData(UnitClassPaladin, GameUnit.PowerMana)]
    [InlineData(UnitClassRogue, GameUnit.PowerEnergy)]
    [InlineData(UnitClassMage, GameUnit.PowerMana)]
    public void PowerType_FollowsTheUnitClass(byte unitClass, byte expected)
    {
        Creature creature = Build(template: Template() with { UnitClass = unitClass });

        Assert.Equal(expected, creature.PowerType);
    }

    /// <summary>
    /// Health is <c>ceil(base × mod)</c>, not a truncating cast.
    /// </summary>
    /// <remarks>
    /// The distinction only shows up on a low-level creature with a modifier below 1: 42 × 0.01
    /// truncates to zero, and a creature with no health is drawn dead and cannot be attacked.
    /// </remarks>
    [Fact]
    public void Health_IsRoundedUpAndNeverZero()
    {
        Creature creature = Build(
            template: Template() with { HealthModifier = 0.01f },
            stats: Stats() with { BaseHealthClassic = 42 });

        Assert.Equal(1u, creature.MaxHealth);
        Assert.Equal(creature.MaxHealth, creature.Health);
    }

    [Fact]
    public void Health_UsesTheExpansionSlotTheTemplateNames()
    {
        CreatureBaseStats stats = Stats() with
        {
            BaseHealthClassic = 100,
            BaseHealthBurningCrusade = 200,
            BaseHealthWrath = 300,
        };

        Assert.Equal(100u, Build(template: Template() with { Expansion = 0 }, stats: stats).MaxHealth);
        Assert.Equal(200u, Build(template: Template() with { Expansion = 1 }, stats: stats).MaxHealth);
        Assert.Equal(300u, Build(template: Template() with { Expansion = 2 }, stats: stats).MaxHealth);
    }

    /// <summary>Zero mana is meaningful — a warrior creature has none — so it is not floored at 1.</summary>
    [Fact]
    public void Mana_StaysZeroWhenTheBaseIsZero()
    {
        Creature creature = Build(stats: Stats() with { BaseMana = 0 });

        Assert.Equal(0u, creature.MaxPower);
        Assert.Equal(0u, creature.Power);
    }

    /// <summary>
    /// A creature that regenerates spawns full; one that does not keeps the row's saved health,
    /// which is how a scripted encounter spawns something already wounded.
    /// </summary>
    [Fact]
    public void SavedHealth_IsHonouredOnlyWhenTheCreatureDoesNotRegenerate()
    {
        CreatureBaseStats stats = Stats() with { BaseHealthClassic = 100 };

        Creature regenerating = Build(
            spawn: Spawn() with { CurrentHealth = 30 },
            template: Template() with { RegeneratesHealth = true },
            stats: stats);

        Creature wounded = Build(
            spawn: Spawn() with { CurrentHealth = 30 },
            template: Template() with { RegeneratesHealth = false },
            stats: stats);

        Assert.Equal(100u, regenerating.Health);
        Assert.Equal(30u, wounded.Health);
        Assert.Equal(100u, wounded.MaxHealth);
    }

    /// <summary>
    /// The spawn row <i>replaces</i> the template's flags rather than adding to them.
    /// </summary>
    /// <remarks>
    /// <c>ObjectMgr::ChooseCreatureFlags</c> is <c>if (data->npcflag) npcflag = data->npcflag</c>.
    /// OR-ing them instead would silently hand creatures abilities — vendor, trainer, quest giver —
    /// that the spawn was written specifically to take away.
    /// </remarks>
    [Fact]
    public void SpawnFlags_ReplaceTheTemplateFlagsRatherThanCombining()
    {
        Creature creature = Build(
            spawn: Spawn() with { NpcFlags = 0x02, UnitFlags = 0x200, DynamicFlags = 0x08 },
            template: Template() with { NpcFlags = 0x81, UnitFlags = 0x100, DynamicFlags = 0x04 });

        Assert.Equal(0x02u, creature.NpcFlags);
        Assert.Equal(0x200u, creature.UnitFlags);
        Assert.Equal(0x08u, creature.DynamicFlags);
    }

    [Fact]
    public void SpawnFlagsOfZero_FallBackToTheTemplate()
    {
        Creature creature = Build(
            spawn: Spawn() with { NpcFlags = 0, UnitFlags = 0, DynamicFlags = 0 },
            template: Template() with { NpcFlags = 0x81, UnitFlags = 0x100, DynamicFlags = 0x04 });

        Assert.Equal(0x81u, creature.NpcFlags);
        Assert.Equal(0x100u, creature.UnitFlags);
        Assert.Equal(0x04u, creature.DynamicFlags);
    }

    /// <summary>Both size fields are multiplied by the template's scale, and neither may stay zero.</summary>
    [Fact]
    public void BoundingRadiusAndCombatReach_AreScaled()
    {
        Creature creature = Build(template: Template() with { Scale = 2.0f });

        Assert.Equal(SampleBoundingRadius * 2.0f, creature.BoundingRadius, 0.0001f);
        Assert.Equal(SampleCombatReach * 2.0f, creature.CombatReach, 0.0001f);
        Assert.Equal(2.0f, creature.ObjectScale, 0.0001f);
    }

    /// <summary>A model with no combat reach of its own falls back to <c>DEFAULT_COMBAT_REACH</c>.</summary>
    [Fact]
    public void CombatReach_FallsBackToTheDefaultWhenTheModelHasNone()
    {
        ICreatureModelSource models = Models(model: Model() with { CombatReach = 0f });
        Creature creature = Build(models: models);

        Assert.Equal(UnitDefaults.CombatReach, creature.CombatReach, 0.0001f);
    }

    /// <summary>
    /// Swapping to the opposite-gender display id must swap its model info too, or a male model ends
    /// up wearing a female hitbox.
    /// </summary>
    [Fact]
    public void OppositeGenderModel_SwapsTheSizeWithTheDisplayId()
    {
        ICreatureModelSource models = Models(
            model: Model() with { Gender = 0, DisplayIdOtherGender = OtherDisplayId },
            otherGender: new CreatureModelInfo(OtherDisplayId, 0.75f, 2.5f, 1, SampleDisplayId));

        Creature swapped = Build(models: models, useOppositeGenderModel: true);
        Creature kept = Build(models: models, useOppositeGenderModel: false);

        Assert.Equal(OtherDisplayId, swapped.DisplayId);
        Assert.Equal(OtherDisplayId, swapped.NativeDisplayId);
        Assert.Equal(1, swapped.Gender);
        Assert.Equal(0.75f, swapped.BoundingRadius, 0.0001f);

        Assert.Equal(SampleDisplayId, kept.DisplayId);
        Assert.Equal(0, kept.Gender);
    }

    /// <summary>A display id with no model info is refused rather than spawned unclickable.</summary>
    [Fact]
    public void UnknownDisplayId_RefusesToBuild()
    {
        Creature? creature = Creature.Create(
            Spawn(), Template(), Models(), Stats(), level: 5, useOppositeGenderModel: false,
            displayId: 999999);

        Assert.Null(creature);
    }

    [Fact]
    public void Speeds_ScaleTheBaseWalkAndRunRates()
    {
        Creature creature = Build(template: Template() with { SpeedWalk = 1.0f, SpeedRun = 1.14286f });

        Assert.Equal(2.5f, creature.Speeds.Walk, 0.0001f);
        Assert.Equal(8.0f, creature.Speeds.Run, 0.001f);
    }

    /// <summary>The movement block has to carry the spawn position, not the origin.</summary>
    [Fact]
    public void Position_ReachesTheMovementBlock()
    {
        Creature creature = Build(spawn: Spawn() with { Position = new Position(-8913.2f, 554.6f, 93.7f, 1.5f) });

        Assert.Equal(-8913.2f, creature.Movement.Position.X, 0.001f);
        Assert.Equal(554.6f, creature.Movement.Position.Y, 0.001f);
        Assert.Equal(93.7f, creature.Movement.Position.Z, 0.001f);
    }

    /// <summary>
    /// The level range is ordered before the roll, because a handful of templates carry a maximum
    /// below their minimum and an unordered draw would come from an empty range.
    /// </summary>
    [Fact]
    public void RollLevel_OrdersTheRangeBeforeDrawing()
    {
        (uint Low, uint High) drawn = default;

        byte level = Creature.RollLevel(
            Template() with { MinLevel = 40, MaxLevel = 10 },
            (low, high) =>
            {
                drawn = (low, high);
                return low;
            });

        Assert.Equal((10u, 40u), drawn);
        Assert.Equal(10, level);
    }

    /// <summary>A single-level template must not consume a draw — the range has one member.</summary>
    [Fact]
    public void RollLevel_DoesNotDrawWhenTheRangeIsOneLevel()
    {
        int draws = 0;

        byte level = Creature.RollLevel(
            Template() with { MinLevel = 17, MaxLevel = 17 },
            (_, _) =>
            {
                draws++;
                return 0;
            });

        Assert.Equal(17, level);
        Assert.Equal(0, draws);
    }

    /// <summary>Zero slots are skipped, so an entry with one model always returns that one.</summary>
    [Fact]
    public void GetRandomValidModelId_SkipsEmptySlots()
    {
        CreatureTemplate single = Template() with
        {
            ModelId1 = 0,
            ModelId2 = 4321,
            ModelId3 = 0,
            ModelId4 = 0,
        };

        Assert.Equal(4321u, single.GetRandomValidModelId((low, _) => low));

        CreatureTemplate none = Template() with
        {
            ModelId1 = 0,
            ModelId2 = 0,
            ModelId3 = 0,
            ModelId4 = 0,
        };

        Assert.Equal(0u, none.GetRandomValidModelId((low, _) => low));
    }

    [Fact]
    public void GetRandomValidModelId_DrawsAcrossOnlyTheValidSlots()
    {
        CreatureTemplate template = Template() with
        {
            ModelId1 = 11,
            ModelId2 = 0,
            ModelId3 = 33,
            ModelId4 = 0,
        };

        (uint Low, uint High) drawn = default;

        uint picked = template.GetRandomValidModelId((low, high) =>
        {
            drawn = (low, high);
            return high;
        });

        // Two valid models, so the draw is over [0, 1] — not [0, 3].
        Assert.Equal((0u, 1u), drawn);
        Assert.Equal(33u, picked);
    }

    /// <summary>The mask is a bit per difficulty, checked against the map's own.</summary>
    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(2, 0, false)]
    [InlineData(2, 1, true)]
    [InlineData(3, 0, true)]
    [InlineData(3, 1, true)]
    public void SpawnMask_GatesByDifficulty(byte mask, byte difficulty, bool expected)
    {
        CreatureSpawn spawn = Spawn() with { SpawnMask = mask };

        Assert.Equal(expected, spawn.SpawnsAtDifficulty(difficulty));
    }

    /// <summary>
    /// A creature's create block, walked section by section.
    /// </summary>
    /// <remarks>
    /// Two things differ from a player's and both are load-bearing. The update type is
    /// <see cref="UpdateType.CreateObject"/>, not <c>CreateObject2</c> — upstream reserves the
    /// second for players, corpses and dynamic objects. And the mask is 19 blocks rather than 42,
    /// because a creature's field block ends at <c>UNIT_END</c>. Nothing in the block carries a
    /// length, so either being wrong shifts every byte after it.
    /// </remarks>
    [Fact]
    public void CreateBlock_IsAUnitBlockNotAPlayerOne()
    {
        Creature creature = Build();

        byte[] block = UpdateBlockBuilder.BuildCreateBlock(
            creature.Guid,
            creature.TypeId,
            creature.Fields,
            creature.Movement,
            creature.Speeds,
            isSelf: false);

        PacketReader reader = new(block);

        Assert.True(reader.TryReadUInt8(out byte updateType));
        Assert.Equal((byte)UpdateType.CreateObject, updateType);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid readGuid));
        Assert.Equal(creature.Guid, readGuid);

        Assert.True(reader.TryReadUInt8(out byte typeId));
        Assert.Equal((byte)TypeId.Unit, typeId);

        // Upstream sets both of these in the Unit constructor, so a creature carries the same pair
        // a player does — minus Self, which only the observer's own character gets.
        Assert.True(reader.TryReadUInt16(out ushort updateFlags));
        Assert.Equal((ushort)(UpdateFlag.Living | UpdateFlag.StationaryPosition), updateFlags);

        // Movement block: flags, extra flags, time, position, orientation, fall time.
        Assert.True(reader.TryReadUInt32(out _));
        Assert.True(reader.TryReadUInt16(out _));
        Assert.True(reader.TryReadUInt32(out _));

        Assert.Equal(-14467.8f, ReadFloat(ref reader), 0.01f);
        Assert.Equal(468.374f, ReadFloat(ref reader), 0.01f);
        Assert.Equal(15.1064f, ReadFloat(ref reader), 0.01f);
        Assert.Equal(0.139626f, ReadFloat(ref reader), 0.0001f);

        Assert.True(reader.TryReadUInt32(out _));

        for (int i = 0; i < 9; i++)
        {
            ReadFloat(ref reader);
        }

        Assert.True(reader.TryReadUInt8(out byte blockCount));
        Assert.Equal((UpdateFields.UNIT_END + 31) / 32, blockCount);

        // The rest is mask words and values; that they add up is what the reader ending clean says.
        for (int i = 0; i < blockCount; i++)
        {
            Assert.True(reader.TryReadUInt32(out _));
        }

        while (reader.Remaining > 0)
        {
            Assert.True(reader.TryReadUInt32(out _));
        }

        Assert.True(reader.Ok);
    }

    private static float ReadFloat(ref PacketReader reader)
    {
        Assert.True(reader.TryReadUInt32(out uint bits));
        return BitConverter.UInt32BitsToSingle(bits);
    }

    // ------------------------------------------------------------------ fixtures

    private const uint SampleEntry = 2843;
    private const uint SampleSpawnId = 17;
    private const uint SampleDisplayId = 4481;
    private const uint OtherDisplayId = 4482;
    private const byte SampleGender = 0;
    private const float SampleBoundingRadius = 0.372f;
    private const float SampleCombatReach = 1.5f;

    private const byte UnitClassWarrior = 1;
    private const byte UnitClassPaladin = 2;
    private const byte UnitClassRogue = 4;
    private const byte UnitClassMage = 8;

    private static Creature Build(
        CreatureSpawn? spawn = null,
        CreatureTemplate? template = null,
        ICreatureModelSource? models = null,
        CreatureBaseStats? stats = null,
        byte level = 5,
        bool useOppositeGenderModel = false)
    {
        Creature? creature = Creature.Create(
            spawn ?? Spawn(),
            template ?? Template(),
            models ?? Models(),
            stats ?? Stats(),
            level,
            useOppositeGenderModel,
            SampleDisplayId);

        Assert.NotNull(creature);
        return creature;
    }

    private static CreatureSpawn Spawn() => new(
        SpawnId: SampleSpawnId,
        Entry: SampleEntry,
        MapId: 0,
        SpawnMask: 1,
        PhaseMask: 1,
        ModelId: 0,
        Position: new Position(-14467.8f, 468.374f, 15.1064f, 0.139626f),
        CurrentHealth: 1,
        CurrentMana: 0,
        NpcFlags: 0,
        UnitFlags: 0,
        DynamicFlags: 0,
        WanderDistance: 0f,
        MovementType: 0,
        RespawnDelaySeconds: 120);

    private static CreatureTemplate Template() => new(
        Entry: SampleEntry,
        Name: "Grimclaw",
        SubName: string.Empty,
        ModelId1: SampleDisplayId,
        ModelId2: 0,
        ModelId3: 0,
        ModelId4: 0,
        MinLevel: 5,
        MaxLevel: 5,
        Expansion: 0,
        Faction: 35,
        NpcFlags: 0,
        SpeedWalk: 1.0f,
        SpeedRun: 1.14286f,
        Scale: 1.0f,
        Rank: 0,
        UnitClass: UnitClassWarrior,
        UnitFlags: 0,
        UnitFlags2: 2048,
        DynamicFlags: 0,
        CreatureType: 1,
        TypeFlags: 0,
        Family: 0,
        HealthModifier: 1.0f,
        ManaModifier: 1.0f,
        ArmorModifier: 1.0f,
        MovementType: 0,
        RegeneratesHealth: true,
        MinDamage: 4f,
        MaxDamage: 6f,
        DamageModifier: 1f,
        BaseAttackTime: 2000,
        RangeAttackTime: 2000,
        AttackPower: 14,
        RangedAttackPower: 0,
        FlagsExtra: 0);

    private static CreatureModelInfo Model() =>
        new(SampleDisplayId, SampleBoundingRadius, SampleCombatReach, SampleGender, 0);

    private static CreatureBaseStats Stats() => new(
        BaseHealthClassic: 100,
        BaseHealthBurningCrusade: 200,
        BaseHealthWrath: 300,
        BaseMana: 50,
        BaseArmor: 60,
        AttackPower: 20,
        RangedAttackPower: 5,
        BaseDamageClassic: 1.5f,
        BaseDamageBurningCrusade: 2f,
        BaseDamageWrath: 3f);

    private static StubModelSource Models(
        CreatureModelInfo? model = null,
        CreatureModelInfo? otherGender = null)
    {
        StubModelSource source = new();
        source.Add(model ?? Model());

        if (otherGender is not null)
        {
            source.Add(otherGender.Value);
        }

        return source;
    }

    /// <summary>Just enough of a model source to build a creature without a database.</summary>
    private sealed class StubModelSource : ICreatureModelSource
    {
        private readonly Dictionary<uint, CreatureModelInfo> _models = [];

        public void Add(CreatureModelInfo model) => _models[model.DisplayId] = model;

        public bool TryGetModel(uint displayId, out CreatureModelInfo model) =>
            _models.TryGetValue(displayId, out model);
    }
}
