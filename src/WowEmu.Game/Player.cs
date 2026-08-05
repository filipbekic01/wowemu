using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game.Maps;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// A player character in the world.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Player</c> that M3 needs: enough update fields for the client to render
/// a character standing in the world with the right model, name, level and stats.
/// <para>
/// Everything the client sees lives in <see cref="GameObjectBase.Fields"/>. The properties here are
/// conveniences over those indices, not a second copy — writing <see cref="Level"/> writes the
/// field, because a value that lives anywhere else would not reach the client.
/// </para>
/// </remarks>
public sealed class Player : Unit
{
    private Player(ObjectGuid guid)
        : base(guid, TypeId.Player, UpdateFields.PLAYER_END, TypeMask.PlayerObject)
    {
    }

    /// <summary>How to reach this player's client. Null for a player with no session.</summary>
    public IPlayerConnection? Connection { get; set; }

    /// <summary>
    /// Guids this player's client has been told about and has not been told to forget.
    /// </summary>
    /// <remarks>
    /// The server has to track this because the client cannot be asked. Sending a create for
    /// something already visible makes it flicker; forgetting to send a destroy leaves a ghost
    /// standing where a player used to be.
    /// </remarks>
    public HashSet<ObjectGuid> VisibleObjects { get; } = [];

    // ------------------------------------------------------------------ combat inputs
    //
    // A player's defences are on the character sheet, so they are read back out of the update fields
    // the client is already shown rather than recomputed. Whatever the tooltip says is what the
    // attack table rolls against, by construction — the two cannot disagree.

    /// <inheritdoc/>
    public override bool IsPlayerControlled => true;

    /// <inheritdoc/>
    public override float DodgeChance => Fields.GetFloat(UpdateFields.PLAYER_DODGE_PERCENTAGE);

    /// <inheritdoc/>
    /// <remarks>
    /// Parry needs a weapon and a class that can do it; without equipment the sheet reads zero, which
    /// is the right answer rather than a missing one.
    /// </remarks>
    public override float ParryChance => Fields.GetFloat(UpdateFields.PLAYER_PARRY_PERCENTAGE);

    /// <inheritdoc/>
    /// <remarks>Zero without a shield, which is every player until equipment exists.</remarks>
    public override float BlockChance => Fields.GetFloat(UpdateFields.PLAYER_BLOCK_PERCENTAGE);

    /// <inheritdoc/>
    public override float CritChanceFor(WeaponAttackType attackType) => attackType switch
    {
        WeaponAttackType.OffAttack => Fields.GetFloat(UpdateFields.PLAYER_OFFHAND_CRIT_PERCENTAGE),
        WeaponAttackType.RangedAttack => Fields.GetFloat(UpdateFields.PLAYER_RANGED_CRIT_PERCENTAGE),
        _ => Fields.GetFloat(UpdateFields.PLAYER_CRIT_PERCENTAGE),
    };

    /// <summary>
    /// Builds a player from everything that describes it: the saved row, the race's client data,
    /// and the level's base stats.
    /// </summary>
    /// <remarks>
    /// The display id comes from <paramref name="race"/> and is the single field that decides
    /// whether the character renders at all — a zero there produces an invisible character with no
    /// error anywhere.
    /// </remarks>
    public static Player Create(
        CharacterSummary character,
        ChrRacesEntry race,
        ChrClassesEntry characterClass,
        PlayerBaseStats stats)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(race);
        ArgumentNullException.ThrowIfNull(characterClass);

        Player player = new(ObjectGuid.Create(HighGuid.Player, character.Id))
        {
            Name = character.Name,
            MapId = character.Map,
            ZoneId = character.Zone,
            Position = new Position(character.PositionX, character.PositionY, character.PositionZ, 0f),
        };

        UpdateFieldStorage fields = player.Fields;

        // UNIT_FIELD_BYTES_0 packs four values into one slot: race, class, gender, power type.
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 0, character.Race);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 1, character.Class);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 2, character.Gender);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 3, (byte)characterClass.PowerType);

        // Appearance, echoed straight back to the client that chose it.
        fields.SetByte(UpdateFields.PLAYER_BYTES, 0, character.Skin);
        fields.SetByte(UpdateFields.PLAYER_BYTES, 1, character.Face);
        fields.SetByte(UpdateFields.PLAYER_BYTES, 2, character.HairStyle);
        fields.SetByte(UpdateFields.PLAYER_BYTES, 3, character.HairColor);
        fields.SetByte(UpdateFields.PLAYER_BYTES_2, 0, character.FacialStyle);

        // Rest state 0x01 in the top byte — "normal", not resting.
        fields.SetByte(UpdateFields.PLAYER_BYTES_2, 3, 0x01);

        fields.SetUInt32(UpdateFields.UNIT_FIELD_LEVEL, character.Level);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_FACTIONTEMPLATE, race.FactionId);

        uint displayId = character.Gender == 0 ? race.MaleDisplayId : race.FemaleDisplayId;
        fields.SetUInt32(UpdateFields.UNIT_FIELD_DISPLAYID, displayId);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_NATIVEDISPLAYID, displayId);

        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, stats.MaxHealth);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_MAXHEALTH, stats.MaxHealth);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_BASE_HEALTH, stats.MaxHealth);

        // Power slot 0 is mana whatever the class's actual resource is; the client reads the one
        // named by the power type in UNIT_FIELD_BYTES_0.
        fields.SetUInt32(UpdateFields.UNIT_FIELD_POWER1, stats.MaxMana);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_MAXPOWER1, stats.MaxMana);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_BASE_MANA, stats.MaxMana);

        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT0, stats.Strength);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT1, stats.Agility);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT2, stats.Stamina);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT3, stats.Intellect);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT4, stats.Spirit);

        // Without a bounding radius and combat reach the client cannot work out where the character
        // physically is, and collision and targeting misbehave.
        fields.SetFloat(UpdateFields.UNIT_FIELD_BOUNDINGRADIUS, 0.389f);
        fields.SetFloat(UpdateFields.UNIT_FIELD_COMBATREACH, 1.5f);

        fields.SetFloat(UpdateFields.OBJECT_FIELD_SCALE_X, 1.0f);

        // Watched faction -1 means "none"; the client renders a reputation bar without it.
        fields.SetInt32(UpdateFields.PLAYER_FIELD_WATCHED_FACTION_INDEX, -1);

        player.SyncMovement();
        return player;
    }
}

/// <summary>
/// A character's base stats at its level, assembled from the two world tables that hold them.
/// </summary>
/// <remarks>
/// <c>player_levelstats</c> is per race, class and level; <c>player_classlevelstats</c> is per class
/// and level. Neither alone is enough, which is why they are combined before they reach the player.
/// </remarks>
public readonly record struct PlayerBaseStats(
    uint MaxHealth,
    uint MaxMana,
    uint Strength,
    uint Agility,
    uint Stamina,
    uint Intellect,
    uint Spirit);
