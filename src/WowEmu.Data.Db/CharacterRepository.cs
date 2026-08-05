using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace WowEmu.Data.Db;

/// <summary>A character as the selection screen needs it.</summary>
public sealed record CharacterSummary(
    uint Id,
    string Name,
    byte Race,
    byte Class,
    byte Gender,
    byte Skin,
    byte Face,
    byte HairStyle,
    byte HairColor,
    byte FacialStyle,
    byte Level,
    uint Zone,
    uint Map,
    float PositionX,
    float PositionY,
    float PositionZ,
    uint GuildId,
    uint PlayerFlags,
    ushort AtLoginFlags,
    uint Money = 0,
    uint Experience = 0);

/// <summary>Reads and writes the realm's characters.</summary>
public interface ICharacterRepository
{
    /// <summary>Every character on this realm belonging to an account, oldest first.</summary>
    Task<IReadOnlyList<CharacterSummary>> ListForAccountAsync(uint accountId, CancellationToken cancellationToken = default);

    /// <summary>How many characters the account has on this realm.</summary>
    Task<int> CountForAccountAsync(uint accountId, CancellationToken cancellationToken = default);

    /// <summary>Whether a name is already taken. Names are unique realm-wide.</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a character. Returns its new guid, or <see langword="null"/> if the name was taken —
    /// the check and the insert race each other, so the unique index is the real arbiter.
    /// </summary>
    Task<uint?> CreateAsync(CharacterEntity character, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves where a character is and what it has, so logging back in resumes there.
    /// </summary>
    /// <remarks>
    /// Money and experience ride along with the position rather than having their own call: they
    /// are written at exactly the same moments, and two calls is two chances for one to be missed.
    /// </remarks>
    Task SaveProgressAsync(
        uint characterId,
        uint mapId,
        uint zoneId,
        float x,
        float y,
        float z,
        float orientation,
        uint money,
        uint experience,
        byte level,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a character, but only if it belongs to <paramref name="accountId"/>. Returns false
    /// if it does not exist or belongs to someone else — the client sends a guid it chose, so
    /// ownership is checked here rather than trusted.
    /// </summary>
    Task<bool> DeleteAsync(uint accountId, uint characterId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICharacterRepository"/>
public sealed class CharacterRepository(IDbContextFactory<CharactersDbContext> contextFactory) : ICharacterRepository
{
    public async Task<IReadOnlyList<CharacterSummary>> ListForAccountAsync(
        uint accountId,
        CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Characters
            .AsNoTracking()
            .Where(character => character.AccountId == accountId)
            .OrderBy(character => character.Id)
            .Select(character => new CharacterSummary(
                character.Id,
                character.Name,
                character.Race,
                character.Class,
                character.Gender,
                character.Skin,
                character.Face,
                character.HairStyle,
                character.HairColor,
                character.FacialStyle,
                character.Level,
                character.Zone,
                character.Map,
                character.PositionX,
                character.PositionY,
                character.PositionZ,
                character.GuildId,
                character.PlayerFlags,
                character.AtLoginFlags,
                character.Money,
                character.Experience))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountForAccountAsync(uint accountId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Characters
            .CountAsync(character => character.AccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Characters
            .AnyAsync(character => character.Name == name, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<uint?> CreateAsync(CharacterEntity character, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(character);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        context.Characters.Add(character);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The unique index on the name is what actually enforces uniqueness; a prior existence
            // check only narrows the window. Two clients creating "Thrall" at once land here.
            return null;
        }

        return character.Id;
    }

    public async Task SaveProgressAsync(
        uint characterId,
        uint mapId,
        uint zoneId,
        float x,
        float y,
        float z,
        float orientation,
        uint money,
        uint experience,
        byte level,
        CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CharacterEntity? character = await context.Characters
            .SingleOrDefaultAsync(entity => entity.Id == characterId, cancellationToken)
            .ConfigureAwait(false);

        if (character is null)
        {
            return;
        }

        character.Map = mapId;
        character.Zone = zoneId;
        character.PositionX = x;
        character.PositionY = y;
        character.PositionZ = z;
        character.Orientation = orientation;
        character.Money = money;
        character.Experience = experience;
        character.Level = level;
        character.LastLoginAt = DateTime.UtcNow;

        // The character has now been in the world, so it is no longer a first login — the client
        // shows its zone in the character list from here on.
        character.AtLoginFlags = 0;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(uint accountId, uint characterId, CancellationToken cancellationToken = default)
    {
        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CharacterEntity? character = await context.Characters
            .SingleOrDefaultAsync(
                entity => entity.Id == characterId && entity.AccountId == accountId,
                cancellationToken)
            .ConfigureAwait(false);

        if (character is null)
        {
            return false;
        }

        context.Characters.Remove(character);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Wiring for the <c>characters</c> database.</summary>
public static class CharacterDatabase
{
    /// <inheritdoc cref="AuthDatabase.ConnectionStringVariable"/>
    public const string ConnectionStringVariable = "WOWEMU_CHARACTERS_CONNECTION";

    /// <summary>Matches the schema created by <c>docker/mysql-init</c>. Development only.</summary>
    public const string DefaultConnectionString =
        "server=127.0.0.1;port=3306;database=wowemu_characters;user=wowemu;password=wowemu";

    public static IServiceCollection AddCharacterDatabase(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContextFactory<CharactersDbContext>(options => options.UseMySQL(connectionString));
        services.AddSingleton<ICharacterRepository, CharacterRepository>();

        return services;
    }

    /// <inheritdoc cref="AuthDatabase.ResolveConnectionString"/>
    public static string ResolveConnectionString(string? configured = null)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured;
    }
}

/// <inheritdoc cref="DesignTimeAuthDbContextFactory"/>
public sealed class DesignTimeCharactersDbContextFactory : IDesignTimeDbContextFactory<CharactersDbContext>
{
    public CharactersDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<CharactersDbContext> options = new();
        options.UseMySQL(CharacterDatabase.ResolveConnectionString());
        return new CharactersDbContext(options.Options);
    }
}
