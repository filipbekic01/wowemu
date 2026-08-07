using WowEmu.Data.Client;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// What it takes to open a locked thing.
/// </summary>
/// <remarks>
/// The gate in front of almost every chest in the game: 38,481 of the 38,594 spawned chests carry a
/// lock id, so this decides whether that content is reachable at all.
/// </remarks>
public sealed class LockTests
{
    /// <summary>Nothing locked opens freely.</summary>
    [Fact]
    public void SomethingUnlocked_Opens() =>
        Assert.Equal(LockResult.Ok, Locks.CanOpen(Character(), lockId: 0, Table()));

    /// <summary>A key opens the thing it is for.</summary>
    [Fact]
    public void AKey_Opens()
    {
        Player without = Character();
        Assert.Equal(LockResult.Locked, Locks.CanOpen(without, KeyLock, Table()));

        Player with = Character();
        InventoryFixture.Place(with, ItemFixture.Build(entry: TheKey), InventoryFixture.Backpack());

        Assert.Equal(LockResult.Ok, Locks.CanOpen(with, KeyLock, Table()));
    }

    /// <summary>Enough lockpicking opens a picklock lock.</summary>
    [Fact]
    public void EnoughSkill_Opens()
    {
        Player unskilled = Character();
        unskilled.Skills.Set(SkillType.Lockpicking, 0, 24, 300);

        Assert.Equal(LockResult.Locked, Locks.CanOpen(unskilled, SkillLock, Table()));

        Player skilled = Character();
        skilled.Skills.Set(SkillType.Lockpicking, 0, 25, 300);

        Assert.Equal(LockResult.Ok, Locks.CanOpen(skilled, SkillLock, Table()));
    }

    /// <summary>
    /// Any one of the eight cases is enough.
    /// </summary>
    /// <remarks>
    /// A chest can list a key <i>and</i> a skill, and either opens it. Requiring all of them makes
    /// almost every locked thing in the game impossible while the data still looks reasonable.
    /// </remarks>
    [Fact]
    public void AnyOneCase_IsEnough()
    {
        Player picker = Character();
        picker.Skills.Set(SkillType.Lockpicking, 0, 100, 300);

        // Has the skill but not the key, on a lock that lists both.
        Assert.Equal(LockResult.Ok, Locks.CanOpen(picker, EitherLock, Table()));

        Player keyholder = Character();
        InventoryFixture.Place(keyholder, ItemFixture.Build(entry: TheKey), InventoryFixture.Backpack());

        Assert.Equal(LockResult.Ok, Locks.CanOpen(keyholder, EitherLock, Table()));
    }

    /// <summary>
    /// A row with no requirements at all is not locked.
    /// </summary>
    /// <remarks>
    /// Those rows exist. Treating the presence of a row as "locked" refuses every one of them.
    /// </remarks>
    [Fact]
    public void ARowWithNoRequirements_IsNotLocked() =>
        Assert.Equal(LockResult.Ok, Locks.CanOpen(Character(), EmptyLock, Table()));

    /// <summary>A lock id the table has never heard of is not silently opened.</summary>
    [Fact]
    public void AnUnknownLock_IsReportedAsUnknown() =>
        Assert.Equal(LockResult.Unknown, Locks.CanOpen(Character(), 9999, Table()));

    /// <summary>
    /// With no table loaded everything opens.
    /// </summary>
    /// <remarks>
    /// Refusing instead would make every chest in the world dead, which is a worse failure than
    /// letting them open — and it is what happened before locks existed.
    /// </remarks>
    [Fact]
    public void WithNoTable_EverythingOpens() =>
        Assert.Equal(LockResult.Ok, Locks.CanOpen(Character(), KeyLock, locks: null));

    /// <summary>
    /// The index on a skill case is a lock type, not a skill id.
    /// </summary>
    /// <remarks>
    /// Reading it directly asks for skills 1, 2 and 3 — none of which are lockpicking, herbalism or
    /// mining, and all of which exist, so the lookup succeeds and answers about the wrong thing.
    /// </remarks>
    [Fact]
    public void TheSkillCaseIndex_IsALockType()
    {
        Assert.Equal(SkillType.Lockpicking, Locks.SkillFor(LockType.Picklock));
        Assert.Equal(SkillType.Fishing, Locks.SkillFor(LockType.Fishing));
        Assert.NotEqual(LockType.Picklock, SkillType.Lockpicking);

        // A lock type opened by a spell rather than a skill has none.
        Assert.Equal(0u, Locks.SkillFor(99));
    }

    /// <summary>The real table loads with the shape the code assumes.</summary>
    [RequiresClientDataFact]
    public void TheRealTable_Loads()
    {
        DbcStore<LockEntry> locks = DbcStores.Load(ClientData.DbcDirectory).Locks;

        Assert.Equal(388, locks.Count);

        Assert.All(locks.Entries, entry =>
        {
            Assert.Equal(LockEntry.Cases, entry.Types.Length);
            Assert.Equal(LockEntry.Cases, entry.Indices.Length);
            Assert.Equal(LockEntry.Cases, entry.Skills.Length);
        });

        // Something in there is opened by lockpicking, or the skill path is never exercised.
        Assert.Contains(
            locks.Entries,
            entry => entry.Types.Select((type, i) => (type, i))
                .Any(c => c.type == LockEntry.KeySkill && entry.Indices[c.i] == LockType.Picklock));
    }

    private const uint KeyLock = 1;
    private const uint SkillLock = 2;
    private const uint EitherLock = 3;
    private const uint EmptyLock = 4;
    private const uint TheKey = 1234;

    private static Player Character() => InventoryFixture.Player(level: 40, proficiencies: false);

    private static DbcStore<LockEntry> Table() =>
        DbcFixture.Store(
            e => e.Id,
            Lock(KeyLock, [(LockEntry.KeyItem, TheKey, 0)]),
            Lock(SkillLock, [(LockEntry.KeySkill, LockType.Picklock, 25)]),
            Lock(EitherLock, [(LockEntry.KeyItem, TheKey, 0), (LockEntry.KeySkill, LockType.Picklock, 25)]),
            Lock(EmptyLock, []));

    private static LockEntry Lock(uint id, (uint Type, uint Index, uint Skill)[] cases)
    {
        uint[] types = new uint[LockEntry.Cases];
        uint[] indices = new uint[LockEntry.Cases];
        uint[] skills = new uint[LockEntry.Cases];

        for (int i = 0; i < cases.Length; i++)
        {
            (types[i], indices[i], skills[i]) = cases[i];
        }

        return new LockEntry(id, types, indices, skills);
    }
}
