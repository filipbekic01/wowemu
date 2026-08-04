using Microsoft.EntityFrameworkCore;
using WowEmu.Core;
using WowEmu.Cryptography;

namespace WowEmu.Data.Db;

/// <summary>Account lookup and maintenance, backed by the <c>auth</c> database.</summary>
public interface IAccountRepository
{
    /// <summary>Finds an account. The name is normalized for you.</summary>
    Task<AuthAccount?> FindAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Records a successful logon: the new session key and where it came from.</summary>
    Task SaveSessionAsync(
        uint accountId,
        byte[] sessionKey,
        string? remoteAddress,
        ushort build,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an account from a plaintext password. Returns <see langword="null"/> if the name is
    /// already taken.
    /// </summary>
    Task<AuthAccount?> CreateAsync(
        string username,
        string password,
        byte securityLevel = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Re-derives salt and verifier for an existing account. False if it does not exist.</summary>
    Task<bool> SetPasswordAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>Deletes an account. False if it did not exist.</summary>
    Task<bool> DeleteAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Every account, ordered by name.</summary>
    Task<IReadOnlyList<AuthAccountSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>How many accounts exist.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountRepository"/>
/// <remarks>
/// Every method opens its own short-lived context. Logon sessions are long-lived and concurrent, and
/// a <see cref="DbContext"/> is neither thread-safe nor meant to be held open across a network wait.
/// </remarks>
public sealed class AccountRepository(IDbContextFactory<AuthDbContext> contextFactory) : IAccountRepository
{
    public async Task<AuthAccount?> FindAsync(string username, CancellationToken cancellationToken = default)
    {
        string normalized = TextTransform.Utf8ToUpperOnlyLatin(username);

        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AccountEntity? entity = await context.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(account => account.Username == normalized, cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task SaveSessionAsync(
        uint accountId,
        byte[] sessionKey,
        string? remoteAddress,
        ushort build,
        CancellationToken cancellationToken = default)
    {
        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AccountEntity? entity = await context.Accounts
            .SingleOrDefaultAsync(account => account.Id == accountId, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            // The account was deleted between the challenge and the proof. Nothing to record.
            return;
        }

        entity.SessionKey = sessionKey;
        entity.LastLoginAt = DateTime.UtcNow;
        entity.LastIp = remoteAddress;
        entity.LastBuild = build;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthAccount?> CreateAsync(
        string username,
        string password,
        byte securityLevel = 0,
        CancellationToken cancellationToken = default)
    {
        string normalizedUser = TextTransform.Utf8ToUpperOnlyLatin(username);
        string normalizedPassword = TextTransform.Utf8ToUpperOnlyLatin(password);

        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        bool exists = await context.Accounts
            .AnyAsync(account => account.Username == normalizedUser, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            return null;
        }

        (byte[] salt, byte[] verifier) = Srp6.MakeRegistrationData(normalizedUser, normalizedPassword);

        AccountEntity entity = new()
        {
            Username = normalizedUser,
            Salt = salt,
            Verifier = verifier,
            SecurityLevel = securityLevel,
            CreatedAt = DateTime.UtcNow,
        };

        context.Accounts.Add(entity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> SetPasswordAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        string normalizedUser = TextTransform.Utf8ToUpperOnlyLatin(username);
        string normalizedPassword = TextTransform.Utf8ToUpperOnlyLatin(password);

        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AccountEntity? entity = await context.Accounts
            .SingleOrDefaultAsync(account => account.Username == normalizedUser, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        (byte[] salt, byte[] verifier) = Srp6.MakeRegistrationData(normalizedUser, normalizedPassword);

        entity.Salt = salt;
        entity.Verifier = verifier;

        // The old session key was derived from the old password; a client holding it must log in again.
        entity.SessionKey = null;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteAsync(string username, CancellationToken cancellationToken = default)
    {
        string normalized = TextTransform.Utf8ToUpperOnlyLatin(username);

        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AccountEntity? entity = await context.Accounts
            .SingleOrDefaultAsync(account => account.Username == normalized, cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        context.Accounts.Remove(entity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<AuthAccountSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Accounts
            .AsNoTracking()
            .OrderBy(account => account.Username)
            .Select(account => new AuthAccountSummary(
                account.Id,
                account.Username,
                account.SecurityLevel,
                account.CreatedAt,
                account.LastLoginAt,
                account.LastIp,
                account.SessionKey != null))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using AuthDbContext context = await contextFactory
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await context.Accounts.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AuthAccount ToRecord(AccountEntity entity) => new(
        entity.Id,
        entity.Username,
        entity.Salt,
        entity.Verifier,
        entity.SessionKey,
        entity.SecurityLevel,
        entity.Flags);
}
