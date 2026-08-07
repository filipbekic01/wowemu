namespace WowEmu.WorldServer;

/// <summary>
/// Deciding whether a chat line is a command, and splitting it up.
/// </summary>
/// <remarks>
/// Port of <c>ChatHandler::ParseCommands</c>. The four rejections below are all upstream's and each
/// exists because of something a real player types by accident — a command framework that treats
/// "..." as a command called "." is one that eats ordinary conversation.
/// </remarks>
public static class ChatCommandParser
{
    /// <summary>The two prefixes the client's own chat frame passes straight through.</summary>
    public const char DotPrefix = '.';
    public const char BangPrefix = '!';

    /// <summary>
    /// Whether a line is a command, and what of it is the command.
    /// </summary>
    /// <param name="rest">Everything after the prefix, or empty when this is not a command.</param>
    /// <remarks>
    /// Four things that look like commands and are not:
    /// <list type="bullet">
    /// <item>A bare <c>.</c> or <c>!</c> — someone hit a key.</item>
    /// <item>
    /// A doubled prefix, <c>..</c> or <c>!!</c>. Ellipses and emphatic punctuation are ordinary
    /// speech, and eating them makes the framework look broken to everyone who is not a GM.
    /// </item>
    /// <item>A prefix followed by a space, which is a sentence that starts with punctuation.</item>
    /// </list>
    /// </remarks>
    public static bool TryParse(string line, out string rest)
    {
        ArgumentNullException.ThrowIfNull(line);

        rest = string.Empty;

        if (line.Length < 2)
        {
            return false;
        }

        if (line[0] is not (DotPrefix or BangPrefix))
        {
            return false;
        }

        if (line[1] == line[0] || line[1] == ' ')
        {
            return false;
        }

        rest = line[1..];

        return true;
    }

    /// <summary>
    /// Splits a command line into its name and the rest of the arguments.
    /// </summary>
    /// <remarks>
    /// One split, not a full tokenise: the name is up to the first space and everything after it is
    /// the argument string, which each command reads however it likes. A command taking a player
    /// name with a space in it would be impossible otherwise.
    /// </remarks>
    public static (string Name, string Arguments) Split(string rest)
    {
        ArgumentNullException.ThrowIfNull(rest);

        int space = rest.IndexOf(' ', StringComparison.Ordinal);

        return space < 0
            ? (rest, string.Empty)
            : (rest[..space], rest[(space + 1)..].Trim());
    }
}

/// <summary>What an account must be to run a command.</summary>
/// <remarks>
/// Mirrors <c>AccountTypes</c>. Only the levels this phase distinguishes are named — the point is
/// that a command's requirement is stated on the command rather than checked inside it, so a new
/// command cannot forget to check.
/// </remarks>
public static class CommandSecurity
{
    /// <summary>Anyone logged in.</summary>
    public const byte Player = 0;

    /// <summary>A game master. Anything that changes the world sits here or above.</summary>
    public const byte GameMaster = 2;

    /// <summary>Full access.</summary>
    public const byte Administrator = 3;
}

/// <summary>One command: what it is called, who may run it, and what it does.</summary>
/// <param name="Name">Lowercase, matched case-insensitively.</param>
/// <param name="Security">The lowest account level that may run it.</param>
/// <param name="Usage">Shown by <c>.help</c> and when the arguments do not parse.</param>
/// <param name="Run">
/// Returns the lines to send back. An empty list means the command handled its own output.
/// </param>
public sealed record ChatCommand(
    string Name,
    byte Security,
    string Usage,
    Func<CommandContext, IReadOnlyList<string>> Run);

/// <summary>Everything a command is allowed to touch.</summary>
/// <remarks>
/// Passed rather than reached for, so a command cannot quietly acquire a dependency on the session
/// — and so the whole set is testable without a socket.
/// </remarks>
public sealed record CommandContext(
    WowEmu.Game.Player Player,
    WowEmu.Game.Maps.Map Map,
    string Arguments,
    byte Security,
    WorldContent World,
    Func<uint> NextItemGuid);
