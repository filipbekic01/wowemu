using Microsoft.EntityFrameworkCore;

namespace WowEmu.Data.Db;

/// <summary>
/// One row of the <c>characters</c> table.
/// </summary>
/// <remarks>
/// Designed fresh rather than copied: PLAN.md §5.2 keeps <c>world</c> structurally close to
/// upstream because re-curating 309 tables of content is not worth it, but <c>characters</c> is
/// ours to own and gets columns added as each phase needs them.
/// <para>
/// Deliberately absent so far: equipment, inventory, skills, spells, quests, reputation. Phase 5
/// adds them when there is something that reads them. The 3.3.5a <c>characters</c> table upstream
/// has ~90 columns; carrying that shape now would be inventing structure for behaviour that does
/// not exist yet.
/// </para>
/// </remarks>
public sealed class CharacterEntity
{
    /// <summary>
    /// The character's guid counter — the <c>guid</c> column, and the low 32 bits of the player's
    /// <c>ObjectGuid</c>. Named <c>Id</c> rather than <c>Guid</c> because the latter reads as
    /// <see cref="System.Guid"/> to every analyzer and half of every reader.
    /// </summary>
    public uint Id { get; set; }

    /// <summary>Owning account, from the <c>auth</c> database. Not a foreign key: different schema.</summary>
    public uint AccountId { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte Race { get; set; }

    public byte Class { get; set; }

    public byte Gender { get; set; }

    // Appearance, as the client sends it at creation. Meaningless to the server; echoed back so
    // the character renders correctly in the selection screen.
    public byte Skin { get; set; }

    public byte Face { get; set; }

    public byte HairStyle { get; set; }

    public byte HairColor { get; set; }

    public byte FacialStyle { get; set; }

    public byte Level { get; set; } = 1;

    public uint Zone { get; set; }

    public uint Map { get; set; }

    public float PositionX { get; set; }

    public float PositionY { get; set; }

    public float PositionZ { get; set; }

    public float Orientation { get; set; }

    /// <summary>
    /// Health at logout, and the seven powers.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed because they are state, not derivation. A character who logs
    /// out at a sliver of health and comes back full has had a free heal; one who logs out DEAD and
    /// comes back alive has undone the entire death system, corpse and penalty and all.
    /// <para>
    /// Seven powers because the client has seven and a character can carry values in more than one
    /// — a druid's rage and energy both survive a form change.
    /// </para>
    /// </remarks>
    public uint Health { get; set; }

    public uint Power1 { get; set; }
    public uint Power2 { get; set; }
    public uint Power3 { get; set; }
    public uint Power4 { get; set; }
    public uint Power5 { get; set; }
    public uint Power6 { get; set; }
    public uint Power7 { get; set; }

    /// <summary>
    /// Which title is worn, as a bit index. Zero for none.
    /// </summary>
    /// <remarks>
    /// <b>A bit index, not a <c>CharTitles.dbc</c> id</b> — the field the client reads holds the
    /// index, and the two are unrelated numbers.
    /// </remarks>
    public uint ChosenTitle { get; set; }

    /// <summary>
    /// Every title earned, as space-separated bit indices.
    /// </summary>
    /// <remarks>
    /// A string rather than a row of integer columns, matching upstream, because the mask is 192
    /// bits and splitting it across columns puts the boundary in a place nothing else cares about.
    /// </remarks>
    public string? KnownTitles { get; set; }

    /// <summary>When the escalating corpse-reclaim penalty runs out, in unix seconds.</summary>
    /// <remarks>
    /// Without it, logging out and back in resets the penalty — which makes chain-dying free for
    /// anyone willing to sit through a loading screen.
    /// </remarks>
    public long DeathExpireTime { get; set; }

    /// <summary>Player flags echoed in the character list — ghost, resting, hidden helm.</summary>
    public uint PlayerFlags { get; set; }

    /// <summary>
    /// Copper. One column, because silver and gold are only how the client draws it.
    /// </summary>
    /// <remarks>
    /// Separate from the inventory even though both are wealth: money is a field on the character,
    /// not a row in a bag, and a character with no items still has a purse.
    /// </remarks>
    public uint Money { get; set; }

    /// <summary>How much experience the character has towards its next level.</summary>
    public uint Experience { get; set; }

    /// <summary>
    /// Pending at-login actions: first login, forced rename, customize. The client renders the
    /// character differently for several of these, so it is in the list packet.
    /// </summary>
    public ushort AtLoginFlags { get; set; }

    public uint GuildId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// The <c>characters</c> database for one realm.
/// </summary>
/// <remarks>
/// Per realm, as upstream does it — there is no realm column, because a realm's characters live in
/// their own schema. That is also why the world server owns this context and the logon server has
/// never heard of it.
/// </remarks>
public sealed class CharactersDbContext(DbContextOptions<CharactersDbContext> options) : DbContext(options)
{
    public DbSet<CharacterEntity> Characters => Set<CharacterEntity>();

    public DbSet<ItemInstanceEntity> Items => Set<ItemInstanceEntity>();

    public DbSet<CharacterInventoryEntity> Inventory => Set<CharacterInventoryEntity>();

    public DbSet<CharacterQuestEntity> Quests => Set<CharacterQuestEntity>();

    public DbSet<CharacterSpellEntity> Spells => Set<CharacterSpellEntity>();

    public DbSet<CharacterActionEntity> Actions => Set<CharacterActionEntity>();

    public DbSet<CharacterSkillEntity> Skills => Set<CharacterSkillEntity>();

    public DbSet<CharacterHomebindEntity> Homebinds => Set<CharacterHomebindEntity>();

    /// <summary>What every faction thinks of every character. <c>character_reputation</c>.</summary>
    public DbSet<CharacterReputationEntity> Reputation => Set<CharacterReputationEntity>();

    /// <summary>Dailies done since the last reset. <c>character_queststatus_daily</c>.</summary>
    public DbSet<CharacterQuestDailyEntity> DailyQuests => Set<CharacterQuestDailyEntity>();

    /// <summary>Weeklies done since the last reset. <c>character_queststatus_weekly</c>.</summary>
    public DbSet<CharacterQuestWeeklyEntity> WeeklyQuests => Set<CharacterQuestWeeklyEntity>();

    /// <summary>Monthlies done since the last reset. <c>character_queststatus_monthly</c>.</summary>
    public DbSet<CharacterQuestMonthlyEntity> MonthlyQuests => Set<CharacterQuestMonthlyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<CharacterEntity>(entity =>
        {
            entity.ToTable("characters");
            entity.HasKey(character => character.Id);

            entity.Property(character => character.Id).HasColumnName("guid").ValueGeneratedOnAdd();
            entity.Property(character => character.AccountId).HasColumnName("account_id");

            entity.Property(character => character.Name)
                .HasColumnName("name")
                .HasMaxLength(12)                       // the client's own limit
                .UseCollation("utf8mb4_bin")
                .IsRequired();

            entity.Property(character => character.Race).HasColumnName("race");
            entity.Property(character => character.Class).HasColumnName("class");
            entity.Property(character => character.Gender).HasColumnName("gender");
            entity.Property(character => character.Skin).HasColumnName("skin");
            entity.Property(character => character.Face).HasColumnName("face");
            entity.Property(character => character.HairStyle).HasColumnName("hair_style");
            entity.Property(character => character.HairColor).HasColumnName("hair_color");
            entity.Property(character => character.FacialStyle).HasColumnName("facial_style");
            entity.Property(character => character.Level).HasColumnName("level");
            entity.Property(character => character.Zone).HasColumnName("zone");
            entity.Property(character => character.Map).HasColumnName("map");
            entity.Property(character => character.PositionX).HasColumnName("position_x");
            entity.Property(character => character.PositionY).HasColumnName("position_y");
            entity.Property(character => character.PositionZ).HasColumnName("position_z");
            entity.Property(character => character.Orientation).HasColumnName("orientation");

            entity.Property(character => character.ChosenTitle).HasColumnName("chosenTitle");
            entity.Property(character => character.KnownTitles).HasColumnName("knownTitles");

            entity.Property(character => character.Health).HasColumnName("health");
            entity.Property(character => character.Power1).HasColumnName("power1");
            entity.Property(character => character.Power2).HasColumnName("power2");
            entity.Property(character => character.Power3).HasColumnName("power3");
            entity.Property(character => character.Power4).HasColumnName("power4");
            entity.Property(character => character.Power5).HasColumnName("power5");
            entity.Property(character => character.Power6).HasColumnName("power6");
            entity.Property(character => character.Power7).HasColumnName("power7");
            entity.Property(character => character.DeathExpireTime).HasColumnName("death_expire_time");
            entity.Property(character => character.PlayerFlags).HasColumnName("player_flags");
            entity.Property(character => character.Money).HasColumnName("money");
            entity.Property(character => character.Experience).HasColumnName("xp");
            entity.Property(character => character.AtLoginFlags).HasColumnName("at_login_flags");
            entity.Property(character => character.GuildId).HasColumnName("guild_id");
            entity.Property(character => character.CreatedAt).HasColumnName("created_at");
            entity.Property(character => character.LastLoginAt).HasColumnName("last_login_at");

            // Character names are unique across the realm; the client enforces it too, but the
            // client is not the authority.
            entity.HasIndex(character => character.Name).IsUnique().HasDatabaseName("ux_characters_name");
            entity.HasIndex(character => character.AccountId).HasDatabaseName("ix_characters_account");
        });

        modelBuilder.Entity<ItemInstanceEntity>(entity =>
        {
            entity.ToTable("item_instance");
            entity.HasKey(item => item.Id);

            // Never generated: the guid is handed to the client before this row exists.
            entity.Property(item => item.Id).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(item => item.Entry).HasColumnName("item_entry");
            entity.Property(item => item.OwnerId).HasColumnName("owner_guid");
            entity.Property(item => item.Count).HasColumnName("count");
            entity.Property(item => item.Durability).HasColumnName("durability");
            entity.Property(item => item.DurationSeconds).HasColumnName("duration");
            entity.Property(item => item.SpellCharges).HasColumnName("charges").HasMaxLength(64);
            entity.Property(item => item.Flags).HasColumnName("flags");
            entity.Property(item => item.RandomPropertyId).HasColumnName("randomPropertyId");
            entity.Property(item => item.SuffixFactor).HasColumnName("randomSuffix");

            entity.HasIndex(item => item.OwnerId).HasDatabaseName("ix_item_instance_owner");
        });

        modelBuilder.Entity<CharacterInventoryEntity>(entity =>
        {
            entity.ToTable("character_inventory");
            entity.HasKey(row => row.ItemId);

            entity.Property(row => row.ItemId).HasColumnName("item").ValueGeneratedNever();
            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.BagId).HasColumnName("bag");
            entity.Property(row => row.Slot).HasColumnName("slot");

            entity.HasIndex(row => row.CharacterId).HasDatabaseName("ix_character_inventory_owner");
        });

        modelBuilder.Entity<CharacterQuestEntity>(entity =>
        {
            entity.ToTable("character_queststatus");

            // Composite: a character has at most one row per quest, and that is the whole
            // invariant — a second row would let the same quest be both complete and rewarded.
            entity.HasKey(row => new { row.CharacterId, row.QuestId });

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.QuestId).HasColumnName("quest");
            entity.Property(row => row.Status).HasColumnName("status");
            entity.Property(row => row.Slot).HasColumnName("slot");
            entity.Property(row => row.Killed1).HasColumnName("mobcount1");
            entity.Property(row => row.Killed2).HasColumnName("mobcount2");
            entity.Property(row => row.Killed3).HasColumnName("mobcount3");
            entity.Property(row => row.Killed4).HasColumnName("mobcount4");
        });

        modelBuilder.Entity<CharacterSpellEntity>(entity =>
        {
            entity.ToTable("character_spell");

            // Composite: a character knows a spell once or not at all.
            entity.HasKey(row => new { row.CharacterId, row.SpellId });

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.SpellId).HasColumnName("spell");
        });

        modelBuilder.Entity<CharacterHomebindEntity>(entity =>
        {
            entity.ToTable("character_homebind");

            // One per character, so the character's own guid is the key.
            entity.HasKey(row => row.CharacterId);

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.MapId).HasColumnName("mapId");
            entity.Property(row => row.AreaId).HasColumnName("zoneId");
            entity.Property(row => row.PositionX).HasColumnName("posX");
            entity.Property(row => row.PositionY).HasColumnName("posY");
            entity.Property(row => row.PositionZ).HasColumnName("posZ");
        });

        modelBuilder.Entity<CharacterSkillEntity>(entity =>
        {
            entity.ToTable("character_skills");

            // Composite: a character has a skill once, at one value.
            entity.HasKey(row => new { row.CharacterId, row.SkillId });

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.SkillId).HasColumnName("skill");
            entity.Property(row => row.Value).HasColumnName("value");
            entity.Property(row => row.MaxValue).HasColumnName("max");
            entity.Property(row => row.Step).HasColumnName("step");
        });

        modelBuilder.Entity<CharacterReputationEntity>(entity =>
        {
            entity.ToTable("character_reputation");

            // Composite: a character has one standing per faction.
            entity.HasKey(row => new { row.CharacterId, row.FactionId });

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.FactionId).HasColumnName("faction");
            entity.Property(row => row.Standing).HasColumnName("standing");
        });

        modelBuilder.Entity<CharacterQuestDailyEntity>(entity =>
        {
            entity.ToTable("character_queststatus_daily");
            entity.HasKey(row => new { row.CharacterId, row.QuestId });

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.QuestId).HasColumnName("quest");
        });

        modelBuilder.Entity<CharacterQuestWeeklyEntity>(entity =>
        {
            entity.ToTable("character_queststatus_weekly");
            entity.HasKey(row => new { row.CharacterId, row.QuestId });

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.QuestId).HasColumnName("quest");
        });

        modelBuilder.Entity<CharacterQuestMonthlyEntity>(entity =>
        {
            entity.ToTable("character_queststatus_monthly");
            entity.HasKey(row => new { row.CharacterId, row.QuestId });

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.QuestId).HasColumnName("quest");
        });

        modelBuilder.Entity<CharacterActionEntity>(entity =>
        {
            entity.ToTable("character_action");
            entity.HasKey(row => new { row.CharacterId, row.Button });

            entity.Property(row => row.CharacterId).HasColumnName("guid");
            entity.Property(row => row.Button).HasColumnName("button");
            entity.Property(row => row.Action).HasColumnName("action");
            entity.Property(row => row.Type).HasColumnName("type");
        });
    }
}
