using System.Diagnostics.CodeAnalysis;
using WowEmu.Core;
using WowEmu.Data.Db;

namespace WowEmu.Game;

/// <summary>
/// Turns a <c>creature</c> row into a <see cref="Creature"/>.
/// </summary>
/// <remarks>
/// The three lookups a spawn needs — template, model, base stats — and the two random draws it
/// makes, in one place. It sits here rather than in <see cref="Creature"/> so that
/// <see cref="Creature.Create"/> stays a pure function of its inputs and can be tested without a
/// database or a seeded generator.
/// <para>
/// Port of <c>ObjectMgr::ChooseDisplayId</c> plus the store lookups around
/// <c>Creature::InitEntry</c>.
/// </para>
/// </remarks>
public sealed class CreatureFactory(
    CreatureTemplateStore templates,
    CreatureStatsStore stats,
    WaypointStore? waypoints = null,
    CreatureAddonStore? addons = null,
    CreatureEquipStore? equipment = null)
{
    /// <summary>
    /// Builds one creature, or explains why it cannot be built.
    /// </summary>
    /// <remarks>
    /// The reason is worth carrying: "entry 12345 has no model info for display 6789" points at a
    /// missing <c>creature_model_info</c> import, while a silently skipped spawn looks like an empty
    /// zone.
    /// </remarks>
    public bool TryCreate(
        CreatureSpawn spawn,
        [NotNullWhen(true)] out Creature? creature,
        [NotNullWhen(false)] out string? reason)
    {
        creature = null;

        if (!templates.TryGetTemplate(spawn.Entry, out CreatureTemplate? template) || template is null)
        {
            reason = $"no creature_template row for entry {spawn.Entry}";
            return false;
        }

        uint displayId = ChooseDisplayId(spawn, template);

        if (displayId == 0)
        {
            reason = $"entry {spawn.Entry} has no display id in any of its four model slots";
            return false;
        }

        byte level = Creature.RollLevel(template, GameRandom.Urand);

        if (!stats.TryGet(level, template.UnitClass, out CreatureBaseStats baseStats))
        {
            reason =
                $"no creature_classlevelstats row for level {level}, unit class {template.UnitClass}";
            return false;
        }

        // Upstream's 50 % opposite-gender roll. Drawn here, once, so the draw count against a fixed
        // seed matches upstream's — PLAN.md §9 makes seeded differential testing the sharpest tool
        // we have, and it only works if both sides consume the generator the same number of times.
        bool useOppositeGenderModel = GameRandom.Urand(0, 1) == 0;

        creature = Creature.Create(
            spawn, template, templates, baseStats, level, useOppositeGenderModel, displayId,
            PathFor(spawn),

            // Drawn after the gender roll and before nothing else, so the generator is consumed in
            // upstream's order — and only for the 176 spawns that ask for a random outfit.
            equipment?.For(spawn.Entry, spawn.EquipmentId, GameRandom.Urand),
            addons?.For(spawn.SpawnId, spawn.Entry));

        if (creature is null)
        {
            reason = $"entry {spawn.Entry} has no creature_model_info row for display id {displayId}";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// The route a spawn patrols, or null.
    /// </summary>
    /// <remarks>
    /// Two lookups, not one. A spawn's route is named by <c>creature_addon.path_id</c> rather than
    /// by its own guid — the two agree for most patrols, which is exactly why keying on the guid
    /// would appear to work and quietly give the wrong route to the ones where they differ.
    /// <para>
    /// Both stores are optional so that a creature can still be built without a database behind it,
    /// the same way the rest of this class is arranged. A patrolling spawn built without them stands
    /// still rather than failing.
    /// </para>
    /// </remarks>
    private IReadOnlyList<Waypoint>? PathFor(CreatureSpawn spawn)
    {
        if (waypoints is null || addons is null)
        {
            return null;
        }

        uint pathId = addons.PathFor(spawn.SpawnId, spawn.Entry);

        return pathId == 0 ? null : waypoints.Path(pathId);
    }

    /// <summary>
    /// Which model this particular spawn wears.
    /// </summary>
    /// <remarks>
    /// The spawn row wins when it names one, which is how the same entry appears with different
    /// looks in different places. Otherwise one of the template's up-to-four slots is drawn.
    /// </remarks>
    private static uint ChooseDisplayId(CreatureSpawn spawn, CreatureTemplate template) =>
        spawn.ModelId != 0 ? spawn.ModelId : template.GetRandomValidModelId(GameRandom.Urand);
}
