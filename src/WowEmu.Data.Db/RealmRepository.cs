using Microsoft.EntityFrameworkCore;

namespace WowEmu.Data.Db;

/// <summary>The realm list, as stored in the <c>auth</c> database.</summary>
public interface IRealmRepository
{
    /// <summary>Every realm, ordered by id.</summary>
    Task<IReadOnlyList<RealmRegistration>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Points a realm at a different host and port. False if no realm has that id.
    /// </summary>
    Task<bool> SetAddressAsync(
        byte realmId,
        string address,
        ushort port,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRealmRepository"/>
public sealed class RealmRepository(IDbContextFactory<AuthDbContext> contextFactory) : IRealmRepository
{
    public async Task<IReadOnlyList<RealmRegistration>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Realms
            .AsNoTracking()
            .OrderBy(realm => realm.Id)
            .Select(realm => new RealmRegistration(
                realm.Id,
                realm.Name,
                realm.Address,
                realm.Port,
                realm.Type,
                realm.Flags,
                realm.PopulationLevel,
                realm.Timezone,
                realm.AllowedSecurityLevel,
                realm.Build))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> SetAddressAsync(
        byte realmId,
        string address,
        ushort port,
        CancellationToken cancellationToken = default)
    {
        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        RealmEntity? entity = await context.Realms
            .SingleOrDefaultAsync(realm => realm.Id == realmId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        entity.Address = address;
        entity.Port = port;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Which client builds this server accepts.</summary>
public interface IBuildRepository
{
    /// <summary>
    /// Whether <paramref name="build"/> has a row in <c>build_info</c>. An empty table rejects
    /// everything — that is the intended behaviour, not a bug.
    /// </summary>
    Task<bool> IsSupportedAsync(ushort build, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IBuildRepository"/>
public sealed class BuildRepository(IDbContextFactory<AuthDbContext> contextFactory) : IBuildRepository
{
    public async Task<bool> IsSupportedAsync(ushort build, CancellationToken cancellationToken = default)
    {
        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Builds
            .AsNoTracking()
            .AnyAsync(row => row.Build == build, cancellationToken)
            .ConfigureAwait(false);
    }
}
