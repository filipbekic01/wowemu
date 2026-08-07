using WowEmu.Data.Client;
using WowEmu.Game.Combat;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// What a character comes back with after a logout.
/// </summary>
/// <remarks>
/// Position, level, money and experience were already saved; health, powers and the death state
/// were not, and were recomputed from the base tables on every login. That is right for a character
/// being created and wrong for one being loaded — the difference between a derivation and state.
/// </remarks>
public sealed class CharacterVitalsTests
{
    /// <summary>Health comes back where it was left.</summary>
    [Fact]
    public void Health_ComesBackWhereItWasLeft()
    {
        Player restored = Load(saved with { Health = 37 });

        Assert.Equal(37u, restored.Health);
    }

    /// <summary>
    /// A character who logs out dead comes back dead.
    /// </summary>
    /// <remarks>
    /// The one that matters. Without it a loading screen undoes the corpse, the reclaim penalty and
    /// the durability charge together — every death becomes optional.
    /// </remarks>
    [Fact]
    public void AGhost_ComesBackAGhost()
    {
        Player restored = Load(saved with
        {
            Health = 1,
            PlayerFlags = PlayerDeath.PlayerFlagGhost,
        });

        Assert.True(restored.IsGhost);
        Assert.False(restored.IsAlive);
        Assert.Equal(DeathState.Dead, restored.DeathState);
    }

    /// <summary>
    /// And the death state agrees with the ghost flag rather than contradicting it.
    /// </summary>
    /// <remarks>
    /// Restoring the flags alone leaves a character that <c>IsGhost</c> calls a ghost and
    /// <c>IsAlive</c> calls alive — a state nothing else in the codebase knows how to be in, and one
    /// that lets a corpse swing a sword.
    /// </remarks>
    [Fact]
    public void TheDeathState_AgreesWithTheGhostFlag()
    {
        Player alive = Load(saved with { Health = 50, PlayerFlags = 0 });

        Assert.False(alive.IsGhost);
        Assert.True(alive.IsAlive);
    }

    /// <summary>Every power slot survives, not just the class's own.</summary>
    /// <remarks>
    /// A druid holds rage and energy at once, so saving only the "current" resource loses the rest.
    /// </remarks>
    [Fact]
    public void EveryPowerSlot_Survives()
    {
        uint[] powers = new uint[CharacterProgress.PowerCount];
        powers[WowEmu.Game.Unit.PowerMana] = 12;
        powers[WowEmu.Game.Unit.PowerRage] = 34;

        Player restored = Load(saved with { Health = 10, Powers = powers });

        Assert.Equal(12u, restored.GetPower(WowEmu.Game.Unit.PowerMana));
        Assert.Equal(34u, restored.GetPower(WowEmu.Game.Unit.PowerRage));
    }

    /// <summary>
    /// A value above the recomputed maximum is clamped rather than carried.
    /// </summary>
    /// <remarks>
    /// The maximum is rebuilt from the base tables on every login, so a character whose level or
    /// gear changed could otherwise come back holding more health than it can hold — which the
    /// client draws as a bar past the end of itself.
    /// </remarks>
    [Fact]
    public void AValueAboveTheMaximum_IsClamped()
    {
        Player restored = Load(saved with { Health = 999_999 });

        Assert.Equal(restored.MaxHealth, restored.Health);
    }

    /// <summary>
    /// A character that has never been saved keeps its freshly computed values.
    /// </summary>
    /// <remarks>
    /// Health of zero is indistinguishable from a character that logged out dead, so it is read as
    /// "never saved" — which costs one corpse its state, once, on the first login after this landed.
    /// A brand-new character arriving at zero health would be the worse trade.
    /// </remarks>
    [Fact]
    public void ACharacterNeverSaved_KeepsItsComputedValues()
    {
        Player fresh = Load(saved with { Health = 0 });

        Assert.True(fresh.IsAlive);
        Assert.True(fresh.Health > 0);
        Assert.Equal(fresh.MaxHealth, fresh.Health);
    }

    /// <summary>The escalating death penalty survives, so relogging does not clear it.</summary>
    /// <remarks>
    /// It is a window in absolute time, so it decays on its own — but only if the expiry is carried
    /// across. Otherwise chain-dying is free for anyone willing to sit through a loading screen.
    /// </remarks>
    [Fact]
    public void TheDeathPenaltyWindow_Survives()
    {
        Player restored = Load(saved with { Health = 10, DeathExpireTime = 1_700_000_900 });

        Assert.Equal(1_700_000_900, restored.DeathExpireTime);
    }

    /// <summary>And it is carried even for a character with nothing else saved.</summary>
    /// <remarks>
    /// Restored outside the health guard on purpose — the window is meaningful on its own, and a
    /// character whose health happens to be unsaved should not get a free penalty reset with it.
    /// </remarks>
    [Fact]
    public void TheDeathPenaltyWindow_SurvivesEvenWithNothingElseSaved()
    {
        Player restored = Load(saved with { Health = 0, DeathExpireTime = 1_700_000_900 });

        Assert.Equal(1_700_000_900, restored.DeathExpireTime);
    }

    private static readonly CharacterSummary saved = new(
        1, "Revenant", 1, 1, 0, 0, 0, 0, 0, 0, 20, 12, 0, 0f, 0f, 0f, 0, 0, 0);

    private static Player Load(CharacterSummary character)
    {
        ChrRacesEntry race = new(1, 0, 1, 49, 50, 7, 0, 0, "Human", 0);
        ChrClassesEntry characterClass = new(1, 1, "Warrior", 4, 0);
        PlayerBaseStats stats = new(500, 200, 23, 20, 22, 20, 20);

        return Player.Create(character, race, characterClass, stats);
    }
}
