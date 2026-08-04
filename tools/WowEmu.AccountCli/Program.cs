using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WowEmu.AccountCli;
using WowEmu.Data.Db;

// Maintenance CLI for the auth database. Phase 1 seeded accounts from appsettings.json; from
// Phase 2 on, this is how accounts and realms are created and edited.
List<string> arguments = [.. args];

string? connectionOverride = null;
int connectionFlag = arguments.IndexOf("--connection");
if (connectionFlag >= 0)
{
    if (connectionFlag + 1 >= arguments.Count)
    {
        Console.Error.WriteLine("--connection needs a value.");
        return 1;
    }

    connectionOverride = arguments[connectionFlag + 1];
    arguments.RemoveRange(connectionFlag, 2);
}

if (arguments.Count == 0 || arguments[0] is "help" or "--help" or "-h")
{
    Help.Print();
    return arguments.Count == 0 ? 1 : 0;
}

ServiceCollection services = new();
services.AddAuthDatabase(AuthDatabase.ResolveConnectionString(connectionOverride));

await using ServiceProvider provider = services.BuildServiceProvider();

IAccountRepository accounts = provider.GetRequiredService<IAccountRepository>();
IRealmRepository realms = provider.GetRequiredService<IRealmRepository>();

try
{
    return (arguments[0], arguments.Count) switch
    {
        ("account", >= 2) => await Commands.AccountAsync(accounts, arguments),
        ("realm", >= 2) => await Commands.RealmAsync(realms, arguments),
        ("db", 2) when arguments[1] == "migrate" => await Commands.MigrateAsync(provider),
        _ => Help.Unknown(arguments[0]),
    };
}
catch (Exception exception) when (exception is DbUpdateException or InvalidOperationException)
{
    Console.Error.WriteLine($"Database error: {exception.Message}");
    return 3;
}
catch (Exception exception) when (exception.GetType().Namespace?.StartsWith("MySql", StringComparison.Ordinal) == true)
{
    Console.Error.WriteLine($"Cannot reach the auth database: {exception.Message}");
    Console.Error.WriteLine("Is it running?  docker compose up -d");
    return 3;
}
