using WowEmu.Data.Client;
using WowEmu.Game;
using WowEmu.WorldServer;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Telling a command apart from something a player typed.
/// </summary>
/// <remarks>
/// Almost all of this is about <i>not</i> treating speech as a command. A framework that eats "..."
/// is one that looks broken to everyone who is not a GM, and the failure is invisible to whoever
/// wrote it — they only ever type real commands.
/// </remarks>
public sealed class ChatCommandParserTests
{
    /// <summary>Both prefixes are commands.</summary>
    [Theory]
    [InlineData(".gps", "gps")]
    [InlineData("!gps", "gps")]
    [InlineData(".additem 1234 5", "additem 1234 5")]
    public void APrefixedLine_IsACommand(string line, string expected)
    {
        Assert.True(ChatCommandParser.TryParse(line, out string rest));
        Assert.Equal(expected, rest);
    }

    /// <summary>An ordinary sentence is not.</summary>
    [Theory]
    [InlineData("hello there")]
    [InlineData("that costs 5.50")]
    public void AnOrdinaryLine_IsNot(string line) =>
        Assert.False(ChatCommandParser.TryParse(line, out _));

    /// <summary>
    /// Ellipses and emphatic punctuation are speech, not commands.
    /// </summary>
    /// <remarks>
    /// The rejection that matters most: people trail off with "..." and shout with "!!" constantly,
    /// and swallowing those makes chat lose messages for reasons nobody can see.
    /// </remarks>
    [Theory]
    [InlineData("...")]
    [InlineData("..")]
    [InlineData("!!")]
    [InlineData("!!!")]
    public void ADoubledPrefix_IsSpeech(string line) =>
        Assert.False(ChatCommandParser.TryParse(line, out _));

    /// <summary>A bare prefix is somebody hitting a key.</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("!")]
    [InlineData("")]
    public void ABarePrefix_IsNotACommand(string line) =>
        Assert.False(ChatCommandParser.TryParse(line, out _));

    /// <summary>A prefix followed by a space is a sentence that starts with punctuation.</summary>
    [Fact]
    public void APrefixThenASpace_IsNotACommand() =>
        Assert.False(ChatCommandParser.TryParse(". hello", out _));

    /// <summary>
    /// The line splits once, so an argument may contain spaces.
    /// </summary>
    /// <remarks>
    /// Tokenising the whole line up front is the obvious thing and makes a command that takes a
    /// player name or a message impossible to write.
    /// </remarks>
    [Fact]
    public void TheLine_SplitsOnce()
    {
        Assert.Equal(("additem", "1234 5"), ChatCommandParser.Split("additem 1234 5"));
        Assert.Equal(("gps", string.Empty), ChatCommandParser.Split("gps"));
        Assert.Equal(("say", "hello there world"), ChatCommandParser.Split("say  hello there world"));
    }
}

/// <summary>
/// Which commands an account may run.
/// </summary>
public sealed class CommandSecurityTests
{
    /// <summary>
    /// A command above an account's level is reported as not existing.
    /// </summary>
    /// <remarks>
    /// The same answer as a genuinely unknown command, on purpose. Telling a player that
    /// <c>.additem</c> exists but is not for them is an invitation to go looking for a way to run
    /// it; telling them nothing is known by that name is not.
    /// </remarks>
    [Fact]
    public void ACommandAboveYourLevel_LooksLikeItDoesNotExist()
    {
        string refused = Assert.Single(Run("additem", CommandSecurity.Player));
        string unknown = Assert.Single(Run("nosuchthing", CommandSecurity.Administrator));

        Assert.Contains("no such command", refused, StringComparison.Ordinal);
        Assert.Contains("no such command", unknown, StringComparison.Ordinal);
    }

    /// <summary>Every command that changes anything is gated above a plain player.</summary>
    /// <remarks>
    /// A sweep rather than a list, so a command added later cannot quietly ship at player level —
    /// which is the one mistake in this file that hands out gold.
    /// </remarks>
    [Fact]
    public void EverythingThatChangesAnything_IsGated()
    {
        string[] readOnly = ["help", "gps"];

        foreach ((string name, ChatCommand command) in CommandTable.All)
        {
            if (Array.IndexOf(readOnly, name) >= 0)
            {
                continue;
            }

            Assert.True(
                command.Security >= CommandSecurity.GameMaster,
                $"'{name}' changes something and must be gated above a plain player");
        }
    }

    /// <summary>Help lists only what the account may actually run.</summary>
    [Fact]
    public void Help_ListsOnlyWhatYouMayRun()
    {
        IReadOnlyList<string> asPlayer = Run("help", CommandSecurity.Player);
        IReadOnlyList<string> asGm = Run("help", CommandSecurity.GameMaster);

        Assert.Equal(2, asPlayer.Count);
        Assert.True(asGm.Count > asPlayer.Count);

        Assert.DoesNotContain(asPlayer, line => line.Contains("additem", StringComparison.Ordinal));
        Assert.Contains(asGm, line => line.Contains("additem", StringComparison.Ordinal));
    }

    /// <summary>Command names are matched however they are typed.</summary>
    [Fact]
    public void CommandNames_AreCaseInsensitive()
    {
        IReadOnlyList<string> shouted = Run("HELP", CommandSecurity.GameMaster);
        IReadOnlyList<string> typed = Run("help", CommandSecurity.GameMaster);

        Assert.Equal(typed, shouted);
        Assert.DoesNotContain(shouted, line => line.Contains("no such command", StringComparison.Ordinal));
    }

    /// <summary>Every command carries a usage line, since it is what a bad argument prints.</summary>
    [Fact]
    public void EveryCommand_HasAUsageLine()
    {
        foreach ((string name, ChatCommand command) in CommandTable.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(command.Usage),
                $"'{name}' has no usage line");
        }
    }

    /// <summary>
    /// Taking away more money than someone has leaves them at zero, not at four billion.
    /// </summary>
    /// <remarks>
    /// Money is unsigned. A subtraction past zero wraps to roughly forty-two thousand gold, which is
    /// the most expensive off-by-one on offer and looks exactly like a working command until someone
    /// tries it on a poor character.
    /// </remarks>
    [Fact]
    public void TakingMoreMoneyThanTheyHave_StopsAtZero()
    {
        Player player = Broke(money: 50);

        Assert.Single(RunOn(player, "money", "-500"));
        Assert.Equal(0u, player.Money);
    }

    /// <summary>And adding past the maximum does not wrap either.</summary>
    [Fact]
    public void AddingPastTheMaximum_StopsAtTheMaximum()
    {
        Player player = Broke(money: uint.MaxValue - 10);

        Assert.Single(RunOn(player, "money", "1000"));
        Assert.Equal(uint.MaxValue, player.Money);
    }

    /// <summary>Money can be given as well as taken.</summary>
    [Fact]
    public void MoneyCanBeGiven()
    {
        Player player = Broke(money: 100);

        RunOn(player, "money", "400");

        Assert.Equal(500u, player.Money);
    }

    /// <summary>A skill set with no maximum takes the value as its maximum.</summary>
    [Fact]
    public void ASkillWithNoMaximum_TakesTheValueAsItsMaximum()
    {
        Player player = Broke();

        RunOn(player, "setskill", $"{SkillType.Swords} 150");

        Assert.Equal(150, player.Skills.PureValue(SkillType.Swords));
        Assert.Equal(150, player.Skills.PureMaxValue(SkillType.Swords));

        RunOn(player, "setskill", $"{SkillType.Swords} 150 300");

        Assert.Equal(300, player.Skills.PureMaxValue(SkillType.Swords));
    }

    /// <summary>Arguments that do not parse print the usage line rather than doing something.</summary>
    [Theory]
    [InlineData("money", "lots")]
    [InlineData("setskill", "43")]
    [InlineData("setskill", "")]
    [InlineData("level", "0")]
    public void ArgumentsThatDoNotParse_PrintTheUsage(string command, string arguments)
    {
        string reply = Assert.Single(RunOn(Broke(), command, arguments));

        Assert.Equal(CommandTable.All[command].Usage, reply);
    }

    private static Player Broke(uint money = 0)
    {
        Player player = InventoryFixture.Player(level: 10, proficiencies: false);
        player.Money = money;

        return player;
    }

    private static IReadOnlyList<string> RunOn(Player player, string name, string arguments) =>
        CommandTable.Execute(
            name,
            new CommandContext(player, null!, arguments, CommandSecurity.Administrator, null!, null!));

    /// <summary>
    /// Runs a command with no world behind it.
    /// </summary>
    /// <remarks>
    /// Only the commands that touch neither the map nor the content stores can be driven this way,
    /// which is why the tests here are about dispatch and security rather than about what each
    /// command does. <c>CommandContext</c> takes null for the rest deliberately: a test that had to
    /// build a map to check a security level would not get written.
    /// </remarks>
    private static IReadOnlyList<string> Run(string name, byte security) =>
        CommandTable.Execute(name, new CommandContext(null!, null!, string.Empty, security, null!, null!));
}
