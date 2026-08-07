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
    uint Experience = 0,
    uint Health = 0,
    uint[]? Powers = null,
    long DeathExpireTime = 0,

    /// <summary>The worn title's bit index, and every earned one as space-separated bit indices.</summary>
    uint ChosenTitle = 0,
    string? KnownTitles = null,

    /// <summary>The reset-cost ladder's state, which decays with time and so must be carried.</summary>
    uint ResetTalentsCost = 0,
    long ResetTalentsTime = 0);

/// <summary>
/// Everything about a character that changes while it is being played.
/// </summary>
/// <remarks>
/// A record rather than another eight positional parameters. The save already took ten, and a call
/// site with eighteen floats and uints in a row is one transposition away from writing a character's
/// health into its mana and nobody noticing.
/// </remarks>
/// <param name="Powers">
/// All seven, indexed by power type. A character can hold values in more than one — a druid's rage
/// and energy both survive a form change — so saving only the "current" one loses the others.
/// </param>
public sealed record CharacterProgress(
    uint MapId,
    uint ZoneId,
    float X,
    float Y,
    float Z,
    float Orientation,
    uint Money,
    uint Experience,
    byte Level,
    uint Health,
    uint[] Powers,
    uint PlayerFlags,
    long DeathExpireTime,

    /// <summary>The worn title's bit index, and every earned one as space-separated bit indices.</summary>
    uint ChosenTitle = 0,
    string KnownTitles = "",

    /// <summary>Which spec is being played, how many are owned, and the reset-cost ladder's state.</summary>
    byte ActiveSpec = 0,
    byte SpecCount = 1,
    uint ResetTalentsCost = 0,
    long ResetTalentsTime = 0)
{
    /// <summary>How many power types the client has.</summary>
    public const int PowerCount = 7;
}

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
    /// Saves everything that changed while a character was played, so logging back in resumes it.
    /// </summary>
    /// <remarks>
    /// One call rather than several: all of it is written at exactly the same moment, and every
    /// extra call is another chance for one to be missed on some path.
    /// </remarks>
    Task SaveProgressAsync(
        uint characterId, CharacterProgress progress, CancellationToken cancellationToken = default);

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
                character.Experience,
                character.Health,
                new[]
                {
                    character.Power1, character.Power2, character.Power3, character.Power4,
                    character.Power5, character.Power6, character.Power7,
                },
                character.DeathExpireTime,
                character.ChosenTitle,
                character.KnownTitles,
                character.ResetTalentsCost,
                character.ResetTalentsTime))
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
        uint characterId, CharacterProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        await using CharactersDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        CharacterEntity? character = await context.Characters
            .SingleOrDefaultAsync(entity => entity.Id == characterId, cancellationToken)
            .ConfigureAwait(false);

        if (character is null)
        {
            return;
        }

        character.Map = progress.MapId;
        character.Zone = progress.ZoneId;
        character.PositionX = progress.X;
        character.PositionY = progress.Y;
        character.PositionZ = progress.Z;
        character.Orientation = progress.Orientation;
        character.Money = progress.Money;
        character.Experience = progress.Experience;
        character.Level = progress.Level;
        character.Health = progress.Health;
        character.PlayerFlags = progress.PlayerFlags;
        character.DeathExpireTime = progress.DeathExpireTime;
        character.ChosenTitle = progress.ChosenTitle;
        character.KnownTitles = progress.KnownTitles;
        character.ActiveSpec = progress.ActiveSpec;
        character.SpecCount = progress.SpecCount;
        character.ResetTalentsCost = progress.ResetTalentsCost;
        character.ResetTalentsTime = progress.ResetTalentsTime;
        character.LastLoginAt = DateTime.UtcNow;

        uint[] powers = progress.Powers;

        character.Power1 = At(powers, 0);
        character.Power2 = At(powers, 1);
        character.Power3 = At(powers, 2);
        character.Power4 = At(powers, 3);
        character.Power5 = At(powers, 4);
        character.Power6 = At(powers, 5);
        character.Power7 = At(powers, 6);

        // The character has now been in the world, so it is no longer a first login — the client
        // shows its zone in the character list from here on.
        character.AtLoginFlags = 0;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static uint At(uint[] powers, int index) => index < powers.Length ? powers[index] : 0;

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
