namespace WowEmu.Game.Combat;

/// <summary>
/// <c>creature_template.flags_extra</c> — the per-entry exceptions to the combat rules.
/// </summary>
/// <remarks>
/// Only the bits the melee attack table consults are named here; the column carries around twenty
/// more (civilian, no taunt, immune to npc, and so on) that belong to the systems that read them.
/// <para>
/// These exist because the general rules are wrong for specific creatures. A target dummy that
/// dodged, or a boss whose crushing blows made an encounter unbeatable, is fixed in the data rather
/// than in the formula — so a table that ignores this column produces plausible numbers against the
/// wrong creatures.
/// </para>
/// </remarks>
[Flags]
public enum CreatureFlagsExtra : uint
{
    None = 0,

    /// <summary>Cannot parry.</summary>
    NoParry = 0x00000004,

    /// <summary>Cannot block.</summary>
    NoBlock = 0x00000010,

    /// <summary>Cannot land crushing blows.</summary>
    NoCrushingBlows = 0x00000020,

    /// <summary>Killing it awards no experience.</summary>
    /// <remarks>
    /// Training dummies, event props, and anything else that can be killed repeatedly at no risk.
    /// Ignoring the bit turns each of them into a levelling strategy.
    /// </remarks>
    NoExperience = 0x00000040,

    /// <summary>Cannot land critical strikes.</summary>
    NoCrit = 0x00020000,

    /// <summary>Cannot dodge.</summary>
    NoDodge = 0x00800000,
}
