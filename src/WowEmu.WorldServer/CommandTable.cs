using System.Globalization;
using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;

namespace WowEmu.WorldServer;

/// <summary>
/// The commands this server knows.
/// </summary>
/// <remarks>
/// Deliberately small, and chosen for what it lets a person <i>check</i> rather than for coverage
/// of upstream's several hundred. Everything here answers a question the code could otherwise only
/// be asked through a debugger: where am I standing, what does the terrain think is under me, what
/// happens when I actually have this skill.
/// <para>
/// <b>Every command states its own security level.</b> The dispatcher enforces it, so a command
/// cannot forget to — which is the failure mode that turns a debug helper into a way for anyone to
/// give themselves gold.
/// </para>
/// </remarks>
public static class CommandTable
{
    /// <summary>Every command, keyed by name.</summary>
    public static IReadOnlyDictionary<string, ChatCommand> All { get; } = Build();

    /// <summary>
    /// Runs a command, or explains why it did not run.
    /// </summary>
    /// <remarks>
    /// An unknown command and one the account may not use give the <i>same</i> answer on purpose.
    /// Telling a player that <c>.additem</c> exists but is not for them is an invitation; telling
    /// them nothing is known by that name is not.
    /// </remarks>
    public static IReadOnlyList<string> Execute(string name, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(context);

        if (!All.TryGetValue(name.ToLowerInvariant(), out ChatCommand? command)
            || context.Security < command.Security)
        {
            return [$"There is no such command as '{name}'."];
        }

        return command.Run(context);
    }

    private static Dictionary<string, ChatCommand> Build()
    {
        List<ChatCommand> commands =
        [
            new("help", CommandSecurity.Player, ".help — list the commands you can use", Help),
            new("gps", CommandSecurity.Player, ".gps — where you are, and what is under you", Gps),
            new("additem", CommandSecurity.GameMaster, ".additem <entry> [count]", AddItem),
            new("learn", CommandSecurity.GameMaster, ".learn <spell>", Learn),
            new("setskill", CommandSecurity.GameMaster, ".setskill <skill> <value> [max]", SetSkill),
            new("money", CommandSecurity.GameMaster, ".money <copper> — may be negative", Money),
            new("level", CommandSecurity.GameMaster, ".level <level>", Level),
            new("die", CommandSecurity.GameMaster, ".die — kill yourself outright", Die),
            new("revive", CommandSecurity.GameMaster, ".revive — back to full, where you stand", Revive),
        ];

        Dictionary<string, ChatCommand> table = [];

        foreach (ChatCommand command in commands)
        {
            table[command.Name] = command;
        }

        return table;
    }

    /// <summary>Lists what this account may actually run, not everything that exists.</summary>
    private static List<string> Help(CommandContext context)
    {
        List<string> lines = [];

        foreach (ChatCommand command in All.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (context.Security >= command.Security)
            {
                lines.Add(command.Usage);
            }
        }

        return lines;
    }

    /// <summary>
    /// Where the player is, in every coordinate system that matters.
    /// </summary>
    /// <remarks>
    /// The most useful command in the set while terrain and grids are being worked on: it is the
    /// only way to see the grid and cell a position falls in, and the floor height the server
    /// believes is under someone's feet, without attaching a debugger to a live session.
    /// </remarks>
    private static IReadOnlyList<string> Gps(CommandContext context)
    {
        Position position = context.Player.Position;

        GridCoord grid = MapCoordinates.GridFor(position.X, position.Y);
        CellCoord cell = MapCoordinates.CellFor(position.X, position.Y);

        uint area = context.Map.Terrain.GetAreaId(position.X, position.Y);
        uint zone = context.World.Stores.ZoneFor(area);

        float? floor = context.Map.GetFloor(position.X, position.Y, position.Z);

        return
        [
            string.Create(
                CultureInfo.InvariantCulture,
                $"Map {context.Map.MapId}, zone {zone}, area {area}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"X {position.X:F3} Y {position.Y:F3} Z {position.Z:F3} O {position.Orientation:F3}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Grid [{grid.X}, {grid.Y}]  Cell [{cell.X}, {cell.Y}]"),
            floor is null
                ? "Floor: nothing under you (no terrain loaded here)"
                : string.Create(CultureInfo.InvariantCulture, $"Floor {floor.Value:F3}, {position.Z - floor.Value:F3} above it"),
        ];
    }

    private static IReadOnlyList<string> AddItem(CommandContext context)
    {
        string[] parts = Words(context.Arguments);

        if (parts.Length == 0 || !uint.TryParse(parts[0], CultureInfo.InvariantCulture, out uint entry))
        {
            return [Usage("additem")];
        }

        uint count = 1;

        if (parts.Length > 1 && !uint.TryParse(parts[1], CultureInfo.InvariantCulture, out count))
        {
            return [Usage("additem")];
        }

        if (!context.World.Items.TryGet(entry, out ItemTemplate? template) || template is null)
        {
            return [$"No item template {entry}."];
        }

        InventoryResult result = context.Player.Inventory.Store(
            template, count, context.NextItemGuid, out _);

        return result == InventoryResult.Ok
            ? [$"Added {count} × {template.Name} ({entry})."]
            : [$"Could not add {template.Name}: {result}."];
    }

    private static IReadOnlyList<string> Learn(CommandContext context)
    {
        if (!uint.TryParse(context.Arguments.Trim(), CultureInfo.InvariantCulture, out uint spellId))
        {
            return [Usage("learn")];
        }

        if (!context.Player.Spells.Learn(spellId))
        {
            return [$"Spell {spellId} was already known."];
        }

        // The same path a trainer takes, so a profession learned this way behaves like one bought.
        SkillLearning.LearnSkillsFromSpell(context.Player, context.World.Stores.Skills, spellId);

        return [$"Learned spell {spellId}."];
    }

    /// <summary>
    /// Sets a skill outright.
    /// </summary>
    /// <remarks>
    /// The one command that reaches past a system's own rules rather than driving it, which is the
    /// point — it is how the proficiency and trainer checks get exercised without grinding to the
    /// value that would satisfy them.
    /// </remarks>
    private static IReadOnlyList<string> SetSkill(CommandContext context)
    {
        string[] parts = Words(context.Arguments);

        if (parts.Length < 2
            || !uint.TryParse(parts[0], CultureInfo.InvariantCulture, out uint skill)
            || !ushort.TryParse(parts[1], CultureInfo.InvariantCulture, out ushort value))
        {
            return [Usage("setskill")];
        }

        ushort max = value;

        if (parts.Length > 2 && !ushort.TryParse(parts[2], CultureInfo.InvariantCulture, out max))
        {
            return [Usage("setskill")];
        }

        if (!context.Player.Skills.Set(skill, context.Player.Skills.Step(skill), value, max))
        {
            return ["No room for another skill, or the value was zero."];
        }

        return [$"Skill {skill} set to {value}/{max}."];
    }

    /// <summary>
    /// Adds or takes away money.
    /// </summary>
    /// <remarks>
    /// Signed, and clamped at zero rather than allowed to wrap. Money is unsigned on the wire, so a
    /// subtraction past zero would hand out forty-two thousand gold — the most expensive off-by-one
    /// available.
    /// </remarks>
    private static IReadOnlyList<string> Money(CommandContext context)
    {
        if (!long.TryParse(context.Arguments.Trim(), CultureInfo.InvariantCulture, out long delta))
        {
            return [Usage("money")];
        }

        long updated = Math.Clamp((long)context.Player.Money + delta, 0, uint.MaxValue);

        context.Player.Money = (uint)updated;

        return [$"Money is now {context.Player.Money} copper."];
    }

    private static IReadOnlyList<string> Level(CommandContext context)
    {
        if (!byte.TryParse(context.Arguments.Trim(), CultureInfo.InvariantCulture, out byte level)
            || level == 0)
        {
            return [Usage("level")];
        }

        // Through the same path as a real level-up, so stats, health and the skill ceilings all
        // move together. Setting Level directly leaves a character at level 60 with a level-1 body.
        if (Experience.LevelUpTo(context.Player, level, context.World.Stats) is null)
        {
            return [$"No stats row for level {level}."];
        }

        SkillLearning.UpdateForLevel(context.Player, context.World.Stores.Skills);

        return [$"Now level {level}."];
    }

    private static IReadOnlyList<string> Die(CommandContext context)
    {
        if (!context.Player.IsAlive)
        {
            return ["You are already dead."];
        }

        // Through the map, so death does everything it normally does — durability, the corpse, and
        // telling everything that was fighting you to stop.
        context.Map.KillPlayer(context.Player);

        return ["Killed."];
    }

    private static IReadOnlyList<string> Revive(CommandContext context)
    {
        if (context.Player.IsAlive)
        {
            return ["You are not dead."];
        }

        context.Map.Resurrect(context.Player);

        return ["Revived."];
    }

    private static string Usage(string name) => All[name].Usage;

    private static string[] Words(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
