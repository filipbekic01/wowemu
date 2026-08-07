using WowEmu.Data.Client;

namespace WowEmu.Game;

/// <summary>
/// Which loot table a request is against. <c>LootType</c>.
/// </summary>
/// <remarks>
/// The client reads this and draws a different window for each — a skinning window has no gold row
/// and a fishing one says "fishing" on it. Sending the wrong one is visible immediately.
/// </remarks>
public static class LootKind
{
    public const byte Corpse = 1;
    public const byte Pickpocketing = 2;
    public const byte Fishing = 3;
    public const byte Disenchanting = 4;
    public const byte Skinning = 6;

    /// <summary>
    /// A fishing spot and a junk catch, neither of which the client understands.
    /// </summary>
    /// <remarks>
    /// <b>Sent to the client as <see cref="Fishing"/>.</b> They exist to pick a table server-side;
    /// putting 20 or 22 on the wire gets a window the client has no layout for.
    /// </remarks>
    public const byte FishingHole = 20;
    public const byte FishingJunk = 22;

    /// <summary>What to actually put on the wire for a kind.</summary>
    public static byte OnWire(byte kind) =>
        kind is FishingHole or FishingJunk ? Fishing : kind;
}

/// <summary>
/// Skinning a corpse.
/// </summary>
/// <remarks>
/// Port of <c>Spell::EffectSkinning</c> and the <c>SPELL_EFFECT_SKINNING</c> arm of
/// <c>Spell::CheckCast</c>.
/// </remarks>
public static class Skinning
{
    /// <summary>
    /// Which skill a creature is skinned with. <c>CreatureTemplate::GetRequiredLootSkill</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not always skinning.</b> Three type flags redirect it to herbalism, mining or
    /// engineering — that is how a herb-covered elemental and a mechanical are looted, and
    /// assuming skinning refuses a herbalist the plant they can plainly see.
    /// </remarks>
    public static uint SkillFor(uint typeFlags)
    {
        if ((typeFlags & CreatureTypeFlags.SkinWithHerbalism) != 0)
        {
            return SkillType.Herbalism;
        }

        if ((typeFlags & CreatureTypeFlags.SkinWithMining) != 0)
        {
            return SkillType.Mining;
        }

        return (typeFlags & CreatureTypeFlags.SkinWithEngineering) != 0
            ? SkillType.Engineering
            : SkillType.Skinning;
    }

    /// <summary>
    /// The skill a corpse demands, as the cast check computes it.
    /// </summary>
    /// <remarks>
    /// <b>Two formulas, and which one applies depends on the skinner rather than the corpse.</b>
    /// Below 100 skill the requirement is <c>(level - 10) * 10</c>; at or above it, <c>level * 5</c>
    /// — which is <i>lower</i> for everything past level 20. The effect that follows uses a third,
    /// level-keyed variant for the skill-up roll. They are genuinely different in upstream and
    /// unifying them changes what a low-skilled skinner can touch.
    /// </remarks>
    public static int RequiredSkill(int targetLevel, int skillValue) =>
        skillValue < 100 ? (targetLevel - 10) * 10 : targetLevel * 5;

    /// <summary>
    /// The requirement the skill-up roll is measured against. <c>Spell::EffectSkinning</c>.
    /// </summary>
    /// <remarks>
    /// Floors at zero below level 10 rather than going negative, which the cast check does not do.
    /// A negative requirement would make every low-level corpse a guaranteed skill-up.
    /// </remarks>
    public static int GainRequirement(int targetLevel) => targetLevel switch
    {
        < 10 => 0,
        < 20 => (targetLevel - 10) * 10,
        _ => targetLevel * 5,
    };

    /// <summary>Whether a character can skin a corpse at all.</summary>
    public static bool CanSkin(int targetLevel, int skillValue) =>
        RequiredSkill(targetLevel, skillValue) <= skillValue;
}

/// <summary>Creature type flags this phase reads. <c>CreatureTypeFlags</c>.</summary>
public static class CreatureTypeFlags
{
    public const uint TameableExotic = 0x00010000;
    public const uint SkinWithHerbalism = 0x00000100;
    public const uint SkinWithMining = 0x00000200;
    public const uint SkinWithEngineering = 0x00008000;
}

/// <summary>
/// Picking a pocket.
/// </summary>
/// <remarks>
/// Port of the <c>LOOT_PICKPOCKETING</c> arm of <c>Player::SendLoot</c>. The odd one out among the
/// loot sources: <b>the target has to be alive</b>, and it is the only loot that generates its own
/// gold rather than taking the creature's.
/// </remarks>
public static class Pickpocketing
{
    /// <summary>
    /// The gold a picked pocket yields, in copper.
    /// </summary>
    /// <remarks>
    /// <b>Two independent rolls, one on each level, then multiplied by ten.</b> Rolling once on the
    /// sum gives the same range with the wrong distribution — the real thing is a triangular curve
    /// that rarely pays the maximum, and a single roll pays it as often as anything else.
    /// </remarks>
    public static uint Money(byte targetLevel, byte pickerLevel, Func<int, int> roll, float rate = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(roll);

        int a = roll((targetLevel / 2) + 1);
        int b = roll((pickerLevel / 2) + 1);

        return (uint)(10 * (a + b) * rate);
    }

    /// <summary>
    /// How long a picked pocket stays empty, in seconds.
    /// </summary>
    /// <remarks>
    /// A minute <i>plus</i> the corpse delay and the respawn time, so the pocket cannot refill
    /// before the creature it belongs to could plausibly have been killed and come back. A flat
    /// minute would let a rogue farm one guard indefinitely.
    /// </remarks>
    public static long CooldownSeconds(long corpseDelaySeconds, long respawnSeconds) =>
        60 + corpseDelaySeconds + respawnSeconds;
}

/// <summary>
/// Fishing.
/// </summary>
/// <remarks>
/// Port of the <c>GAMEOBJECT_TYPE_FISHINGNODE</c> arm of <c>GameObject::Update</c>. The catch is
/// decided when the bobber bobs, not when it is cast.
/// </remarks>
public static class Fishing
{
    /// <summary>
    /// How far above a zone's base skill guarantees a catch. Ninety-five, since patch 2.1.
    /// </summary>
    public const int NoMissMargin = 95;

    /// <summary>
    /// The chance of catching something rather than junk, as a percentage.
    /// </summary>
    /// <remarks>
    /// <b>Squared, not linear.</b> Half the required skill gives 25%, not 50% — fishing in water
    /// well above your level is far worse than it looks, and a linear reading makes early fishing
    /// dramatically easier than it should be.
    /// <para>
    /// Floored at 1: even hopeless water gives an occasional catch, and a zero would make a zone
    /// unfishable forever rather than merely painful.
    /// </para>
    /// </remarks>
    public static int SuccessChance(int skill, int zoneSkill)
    {
        int noMiss = zoneSkill + NoMissMargin;

        if (skill >= noMiss)
        {
            return 100;
        }

        int chance = (int)(Math.Pow((double)skill / noMiss, 2) * 100);

        return Math.Max(chance, 1);
    }
}

/// <summary>
/// Disenchanting.
/// </summary>
/// <remarks>
/// Port of <c>Spell::EffectDisEnchant</c>. Two columns on the item and both matter: one says what
/// it turns into and one says how skilled you have to be.
/// </remarks>
public static class Disenchant
{
    /// <summary>
    /// Whether an item can be disenchanted by a character with this much enchanting.
    /// </summary>
    /// <remarks>
    /// <b>A missing loot id is not a skill problem.</b> Most items have no <c>DisenchantID</c> at
    /// all and can never be disenchanted by anyone — reporting that as "your skill is too low"
    /// sends a maxed enchanter off to level a skill that is already maxed.
    /// </remarks>
    public static bool CanDisenchant(uint disenchantId, int requiredSkill, int skillValue) =>
        disenchantId != 0 && skillValue >= requiredSkill;
}
