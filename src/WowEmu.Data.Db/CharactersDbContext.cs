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

    /// <summary>Player flags echoed in the character list — ghost, resting, hidden helm.</summary>
    public uint PlayerFlags { get; set; }

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
            entity.Property(character => character.PlayerFlags).HasColumnName("player_flags");
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
    }
}
