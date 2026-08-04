using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WowEmu.Data.Db;

namespace WowEmu.AccountCli;

/// <summary>The verbs. Each returns the process exit code.</summary>
internal static class Commands
{
    public static async Task<int> AccountAsync(IAccountRepository accounts, IReadOnlyList<string> arguments)
    {
        switch (arguments[1])
        {
            case "create":
            {
                if (arguments.Count < 3)
                {
                    Console.Error.WriteLine("Usage: account create <username> [password] [--security <n>]");
                    return 1;
                }

                string username = arguments[2];
                byte security = ReadSecurityLevel(arguments);
                string? password = ReadPassword(arguments, index: 3);

                if (password is null)
                {
                    return 1;
                }

                AuthAccount? created = await accounts.CreateAsync(username, password, security);

                if (created is null)
                {
                    Console.Error.WriteLine($"Account '{username.ToUpperInvariant()}' already exists.");
                    return 2;
                }

                Console.WriteLine($"Created account '{created.Username}' (id {created.Id}, security {security}).");
                return 0;
            }

            case "set-password":
            {
                if (arguments.Count < 3)
                {
                    Console.Error.WriteLine("Usage: account set-password <username> [password]");
                    return 1;
                }

                string username = arguments[2];
                string? password = ReadPassword(arguments, index: 3);

                if (password is null)
                {
                    return 1;
                }

                if (!await accounts.SetPasswordAsync(username, password))
                {
                    Console.Error.WriteLine($"No such account: '{username}'.");
                    return 2;
                }

                Console.WriteLine($"Password updated for '{username.ToUpperInvariant()}'. Any cached session is now invalid.");
                return 0;
            }

            case "delete":
            {
                if (arguments.Count < 3)
                {
                    Console.Error.WriteLine("Usage: account delete <username>");
                    return 1;
                }

                if (!await accounts.DeleteAsync(arguments[2]))
                {
                    Console.Error.WriteLine($"No such account: '{arguments[2]}'.");
                    return 2;
                }

                Console.WriteLine($"Deleted '{arguments[2].ToUpperInvariant()}'.");
                return 0;
            }

            case "list":
            {
                IReadOnlyList<AuthAccountSummary> all = await accounts.ListAsync();

                if (all.Count == 0)
                {
                    Console.WriteLine("No accounts.");
                    return 0;
                }

                Console.WriteLine($"{"ID",-5} {"USERNAME",-20} {"SEC",-4} {"CREATED",-17} {"LAST LOGIN",-17} LAST IP");
                foreach (AuthAccountSummary account in all)
                {
                    Console.WriteLine(
                        $"{account.Id,-5} {account.Username,-20} {account.SecurityLevel,-4} " +
                        $"{Timestamp(account.CreatedAt),-17} {Timestamp(account.LastLoginAt),-17} " +
                        $"{account.LastIp ?? "-"}");
                }

                return 0;
            }

            default:
                return Help.Unknown($"account {arguments[1]}");
        }
    }

    public static async Task<int> RealmAsync(IRealmRepository realms, IReadOnlyList<string> arguments)
    {
        switch (arguments[1])
        {
            case "list":
            {
                IReadOnlyList<RealmRegistration> all = await realms.ListAsync();

                if (all.Count == 0)
                {
                    Console.WriteLine("No realms. The client will log in and then show an empty list.");
                    return 0;
                }

                Console.WriteLine($"{"ID",-4} {"NAME",-20} {"ADDRESS",-26} {"BUILD",-6} {"TYPE",-5} FLAGS");
                foreach (RealmRegistration realm in all)
                {
                    Console.WriteLine(
                        $"{realm.Id,-4} {realm.Name,-20} {realm.Address + ":" + realm.Port.ToString(CultureInfo.InvariantCulture),-26} " +
                        $"{realm.Build,-6} {realm.Type,-5} 0x{realm.Flags:X2}");
                }

                return 0;
            }

            case "set-address":
            {
                if (arguments.Count < 4)
                {
                    Console.Error.WriteLine("Usage: realm set-address <id> <host> [port]");
                    return 1;
                }

                if (!byte.TryParse(arguments[2], CultureInfo.InvariantCulture, out byte realmId))
                {
                    Console.Error.WriteLine($"'{arguments[2]}' is not a realm id.");
                    return 1;
                }

                string host = arguments[3];
                ushort port = 8085;

                if (arguments.Count > 4 && !ushort.TryParse(arguments[4], CultureInfo.InvariantCulture, out port))
                {
                    Console.Error.WriteLine($"'{arguments[4]}' is not a port.");
                    return 1;
                }

                if (!await realms.SetAddressAsync(realmId, host, port))
                {
                    Console.Error.WriteLine($"No realm with id {realmId}.");
                    return 2;
                }

                Console.WriteLine($"Realm {realmId} now points at {host}:{port}.");
                Console.WriteLine("The running auth server picks this up on its next realm refresh.");
                return 0;
            }

            default:
                return Help.Unknown($"realm {arguments[1]}");
        }
    }

    public static async Task<int> MigrateAsync(IServiceProvider provider)
    {
        IDbContextFactory<AuthDbContext> contextFactory =
            provider.GetRequiredService<IDbContextFactory<AuthDbContext>>();

        await using AuthDbContext context = await contextFactory.CreateDbContextAsync();

        List<string> pending = [.. await context.Database.GetPendingMigrationsAsync()];

        if (pending.Count == 0)
        {
            Console.WriteLine("Schema is up to date.");
            return 0;
        }

        Console.WriteLine($"Applying {pending.Count} migration(s):");
        foreach (string migration in pending)
        {
            Console.WriteLine($"  {migration}");
        }

        await context.Database.MigrateAsync();
        Console.WriteLine("Done.");
        return 0;
    }

    /// <summary>
    /// Takes the password from the argument list, or prompts for it without echoing.
    /// Passing it on the command line puts it in your shell history; prompting does not.
    /// </summary>
    private static string? ReadPassword(IReadOnlyList<string> arguments, int index)
    {
        if (arguments.Count > index && !arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            return arguments[index];
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("No password given and no console to prompt on.");
            return null;
        }

        Console.Write("Password: ");
        string password = ReadHidden();
        Console.Write("Repeat:   ");
        string repeat = ReadHidden();

        if (!string.Equals(password, repeat, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match.");
            return null;
        }

        if (password.Length == 0)
        {
            Console.Error.WriteLine("Password must not be empty.");
            return null;
        }

        return password;
    }

    private static string ReadHidden()
    {
        System.Text.StringBuilder builder = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }

    private static byte ReadSecurityLevel(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == "--security" &&
                byte.TryParse(arguments[i + 1], CultureInfo.InvariantCulture, out byte level))
            {
                return level;
            }
        }

        return 0;
    }

    private static string Timestamp(DateTime? value) =>
        value?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
}

/// <summary>Usage text.</summary>
internal static class Help
{
    public static void Print()
    {
        Console.WriteLine("""
            wowemu-account — maintenance for the WowEmu auth database

            Usage:
              account create <username> [password] [--security <n>]   Create an account
              account set-password <username> [password]              Change a password
              account delete <username>                               Delete an account
              account list                                            List accounts
              realm list                                              List realms
              realm set-address <id> <host> [port]                    Point a realm at a host
              db migrate                                              Apply pending migrations

            Options:
              --connection <string>   Override the connection string
                                      (else WOWEMU_AUTH_CONNECTION, else the docker-compose default)

            Omit the password and you will be prompted for it, which keeps it out of your shell
            history. Usernames and passwords are uppercased before the SRP6 verifier is derived,
            so they are effectively case-insensitive at the login screen.
            """);
    }

    public static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run with --help for usage.");
        return 1;
    }
}
