using Microsoft.EntityFrameworkCore;

namespace WowEmu.Data.Db;

/// <summary>
/// The <c>auth</c> database: accounts, the realm list, and the table of client builds we accept.
/// </summary>
/// <remarks>
/// Table and column names are spelled out rather than left to convention. The schema is a wire
/// contract shared with the world server and with hand-written SQL, so it should not move because
/// someone renamed a property.
/// </remarks>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();

    public DbSet<RealmEntity> Realms => Set<RealmEntity>();

    public DbSet<BuildInfoEntity> Builds => Set<BuildInfoEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AccountEntity>(entity =>
        {
            entity.ToTable("account");
            entity.HasKey(account => account.Id);
            entity.Property(account => account.Id).HasColumnName("id").ValueGeneratedOnAdd();

            entity.Property(account => account.Username)
                .HasColumnName("username")
                .HasMaxLength(32)
                .UseCollation("utf8mb4_bin")
                .IsRequired();

            // binary(N), not varbinary: every value is exactly this wide, always.
            entity.Property(account => account.Salt)
                .HasColumnName("salt").HasColumnType("binary(32)").IsRequired();
            entity.Property(account => account.Verifier)
                .HasColumnName("verifier").HasColumnType("binary(32)").IsRequired();
            entity.Property(account => account.SessionKey)
                .HasColumnName("session_key").HasColumnType("binary(40)");

            entity.Property(account => account.SecurityLevel).HasColumnName("security_level");
            entity.Property(account => account.Flags).HasColumnName("flags");
            entity.Property(account => account.CreatedAt).HasColumnName("created_at");
            entity.Property(account => account.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(account => account.LastIp).HasColumnName("last_ip").HasMaxLength(45);
            entity.Property(account => account.LastBuild).HasColumnName("last_build");

            entity.HasIndex(account => account.Username).IsUnique().HasDatabaseName("ux_account_username");
        });

        modelBuilder.Entity<RealmEntity>(entity =>
        {
            entity.ToTable("realmlist");
            entity.HasKey(realm => realm.Id);

            // The id is ours to choose and is echoed to the client; never auto-generated.
            entity.Property(realm => realm.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(realm => realm.Name).HasColumnName("name").HasMaxLength(32).IsRequired();
            entity.Property(realm => realm.Address).HasColumnName("address").HasMaxLength(255).IsRequired();
            entity.Property(realm => realm.Port).HasColumnName("port");
            entity.Property(realm => realm.Type).HasColumnName("type");
            entity.Property(realm => realm.Flags).HasColumnName("flags");
            entity.Property(realm => realm.PopulationLevel).HasColumnName("population_level");
            entity.Property(realm => realm.Timezone).HasColumnName("timezone");
            entity.Property(realm => realm.AllowedSecurityLevel).HasColumnName("allowed_security_level");
            entity.Property(realm => realm.Build).HasColumnName("build");

            entity.HasData(new RealmEntity
            {
                Id = 1,
                Name = "WowEmu",
                Address = "127.0.0.1",
                Port = 8085,
                Type = 0,
                Flags = 0,
                PopulationLevel = 0f,
                Timezone = 1,
                AllowedSecurityLevel = 0,
                Build = 12340,
            });
        });

        modelBuilder.Entity<BuildInfoEntity>(entity =>
        {
            entity.ToTable("build_info");
            entity.HasKey(build => build.Build);

            entity.Property(build => build.Build).HasColumnName("build").ValueGeneratedNever();
            entity.Property(build => build.MajorVersion).HasColumnName("major_version");
            entity.Property(build => build.MinorVersion).HasColumnName("minor_version");
            entity.Property(build => build.BugfixVersion).HasColumnName("bugfix_version");
            entity.Property(build => build.HotfixLetter).HasColumnName("hotfix_letter").HasMaxLength(1);

            // 3.3.5a. Without this row every login fails on version check.
            entity.HasData(new BuildInfoEntity
            {
                Build = 12340,
                MajorVersion = 3,
                MinorVersion = 3,
                BugfixVersion = 5,
                HotfixLetter = "a",
            });
        });
    }
}
