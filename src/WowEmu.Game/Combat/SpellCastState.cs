using WowEmu.Data.Client;
using WowEmu.Protocol;

namespace WowEmu.Game.Combat;

/// <summary>Where a cast has got to.</summary>
public enum CastState : byte
{
    /// <summary>Nothing is being cast.</summary>
    Idle = 0,

    /// <summary>The cast bar is running.</summary>
    Casting = 1,
}

/// <summary>
/// One cast in progress.
/// </summary>
/// <param name="Spell">What is being cast.</param>
/// <param name="Target">Who at, if anyone.</param>
/// <param name="CastCount">The client's handle for this attempt, which every answer must quote back.</param>
/// <param name="RemainingMs">Milliseconds left on the cast bar.</param>
/// <param name="TotalMs">How long the bar was to begin with.</param>
public readonly record struct PendingCast(
    SpellEntry Spell,
    Unit? Target,
    byte CastCount,
    int RemainingMs,
    int TotalMs)
{
    /// <summary>Whether the bar has run out.</summary>
    public bool IsFinished => RemainingMs <= 0;
}

/// <summary>
/// A unit's casting: the cooldowns it is under and the cast it is part-way through.
/// </summary>
/// <remarks>
/// Port of the timing parts of <c>Spell</c> and <c>SpellHistory</c>. There is no spell queue, no
/// channelling and no pushback — a cast either runs to completion or is cancelled.
/// </remarks>
public sealed class SpellCastState
{
    /// <summary>
    /// The default global cooldown, in milliseconds.
    /// </summary>
    /// <remarks>
    /// A spell's own <c>StartRecoveryTime</c> overrides this. Zero there means the spell does not
    /// trigger a global cooldown at all — Heroic Strike is the usual example — so a nonzero default
    /// applied unconditionally would put every instant ability behind a wait it should not have.
    /// </remarks>
    public const int DefaultGlobalCooldownMs = 1500;

    private readonly Dictionary<uint, int> _cooldowns = [];

    /// <summary>The cast in progress, if any.</summary>
    public PendingCast? Current { get; private set; }

    /// <summary>Whether a cast bar is running.</summary>
    public CastState State => Current is null ? CastState.Idle : CastState.Casting;

    /// <summary>Milliseconds left on the global cooldown.</summary>
    public int GlobalCooldownMs { get; private set; }

    /// <summary>Whether the global cooldown has expired.</summary>
    public bool IsGlobalCooldownReady => GlobalCooldownMs <= 0;

    /// <summary>Milliseconds left on one spell's own cooldown. Zero when it is ready.</summary>
    public int CooldownMs(uint spellId) => _cooldowns.GetValueOrDefault(spellId);

    /// <summary>Whether a spell's own cooldown has expired.</summary>
    public bool IsReady(uint spellId) => CooldownMs(spellId) <= 0;

    /// <summary>Puts a spell on cooldown.</summary>
    public void StartCooldown(uint spellId, int milliseconds)
    {
        if (milliseconds > 0)
        {
            _cooldowns[spellId] = milliseconds;
        }
    }

    /// <summary>
    /// Starts the global cooldown for a spell.
    /// </summary>
    /// <remarks>
    /// A spell with no <c>StartRecoveryTime</c> starts no global cooldown. Substituting the default
    /// for zero is the obvious-looking simplification and it makes rage and energy abilities feel
    /// wrong in a way that is hard to attribute.
    /// </remarks>
    public void StartGlobalCooldown(SpellEntry spell)
    {
        ArgumentNullException.ThrowIfNull(spell);

        if (spell.StartRecoveryTime > 0)
        {
            GlobalCooldownMs = (int)spell.StartRecoveryTime;
        }
    }

    /// <summary>Begins a cast bar.</summary>
    public void Begin(SpellEntry spell, Unit? target, byte castCount, int castTimeMs)
    {
        ArgumentNullException.ThrowIfNull(spell);

        Current = new PendingCast(spell, target, castCount, castTimeMs, castTimeMs);
    }

    /// <summary>Abandons the cast in progress, if there is one.</summary>
    /// <returns>What was abandoned, or null.</returns>
    public PendingCast? Cancel()
    {
        PendingCast? cancelled = Current;
        Current = null;

        return cancelled;
    }

    /// <summary>
    /// Advances every timer by a tick, and reports a cast that finished.
    /// </summary>
    /// <remarks>
    /// The finished cast is returned rather than acted on, so that the decision about what a
    /// completed cast <i>does</i> stays with the caller — this class knows about time, not effects.
    /// <para>
    /// Cooldowns tick whether or not anything is being cast, which is why they are advanced before
    /// the early return.
    /// </para>
    /// </remarks>
    public PendingCast? Update(uint diffMs)
    {
        if (diffMs == 0)
        {
            return null;
        }

        int diff = (int)diffMs;

        GlobalCooldownMs = Math.Max(GlobalCooldownMs - diff, 0);

        if (_cooldowns.Count > 0)
        {
            // Materialised: expired entries are removed, and mutating while enumerating would throw.
            foreach (uint spellId in _cooldowns.Keys.ToList())
            {
                int remaining = _cooldowns[spellId] - diff;

                if (remaining <= 0)
                {
                    _cooldowns.Remove(spellId);
                }
                else
                {
                    _cooldowns[spellId] = remaining;
                }
            }
        }

        if (Current is not { } cast)
        {
            return null;
        }

        PendingCast advanced = cast with { RemainingMs = cast.RemainingMs - diff };

        if (!advanced.IsFinished)
        {
            Current = advanced;
            return null;
        }

        Current = null;

        return advanced;
    }
}
